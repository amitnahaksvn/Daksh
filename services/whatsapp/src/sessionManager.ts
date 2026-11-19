import type { Boom } from "@hapi/boom";
import type { Db } from "mongodb";
import { pino } from "pino";
import {
  DisconnectReason,
  fetchLatestBaileysVersion,
  makeCacheableSignalKeyStore,
  normalizeMessageContent,
  makeWASocket,
  type WAMessage,
  type WASocket,
} from "@whiskeysockets/baileys";
import { removeAllCredentials, useMongoAuthState } from "./authStateMongo.js";
import { contactsCollection, messagesCollection } from "./db.js";
import { reportEvent } from "./apiClient.js";
import {
  STATUS, applyEdit, detectMedia, forgetProfilePicture, getProfilePicture, handleProtocolMessage, markRevoked, mediaLimit,
  onMessagesUpdate, onReceipts, saveContacts, saveLidMapping, saveMediaBytes, storeMedia, type MediaKind,
} from "./extras.js";

export type ConnectionStatus = "Connecting" | "Connected" | "Disconnected" | "Error";

type SessionEntry = {
  sock: WASocket | null;
  status: ConnectionStatus;
  qr?: string;
  qrExpiresAt?: Date;
  accountIdentifier?: string;
  lastError?: string;
  reconnectAttempts: number;
  reconnectTimer?: NodeJS.Timeout;
  /** When WhatsApp recently told us this login was replaced by another connection (another copy of the service,
   *  or a stray session). Used to stop fighting over the login instead of reconnecting forever. */
  replacedAt?: number[];
  /** True while a socket is actively being created, so a second concurrent connect/reconnect call for
   *  the same connectionId waits on this instead of opening a second WhatsApp session. */
  starting: Promise<void> | null;
};

const QR_LIFETIME_MS = 20_000; // Baileys reissues a fresh QR roughly this often until it's scanned.
const MAX_RECONNECT_ATTEMPTS = 10;
const REPLACED_WINDOW_MS = 120_000;
const REPLACED_LIMIT = 3;
const logger = pino({ level: process.env.BAILEYS_LOG_LEVEL ?? "warn" });

const sessions = new Map<string, SessionEntry>();

function entryFor(connectionId: string): SessionEntry {
  let entry = sessions.get(connectionId);
  if (!entry) {
    entry = { sock: null, status: "Disconnected", reconnectAttempts: 0, starting: null };
    sessions.set(connectionId, entry);
  }
  return entry;
}

async function startSocket(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  if (entry.starting) return entry.starting; // another caller is already starting this exact session
  // A second makeWASocket() on top of an already-open one isn't harmless — WhatsApp treats it as a
  // conflicting device and force-logs the real session out. If this one is live, there's nothing to do.
  if (entry.status === "Connected" && entry.sock) return;

  entry.starting = (async () => {
    entry.status = "Connecting";
    await reportEvent(connectionId, "WhatsAppConnectionStarted", { status: "Connecting" });

    const { state, saveCreds } = await useMongoAuthState(db, connectionId);
    const { version } = await fetchLatestBaileysVersion();
    const sock = makeWASocket({
      version,
      auth: { creds: state.creds, keys: makeCacheableSignalKeyStore(state.keys, logger) },
      logger,
      syncFullHistory: false,
    });
    entry.sock = sock;

    sock.ev.on("creds.update", saveCreds);
    sock.ev.on("connection.update", (update) => void onConnectionUpdate(db, connectionId, entry, update));
    sock.ev.on("messages.upsert", (payload) => void onMessagesUpsert(db, connectionId, payload));
    sock.ev.on("contacts.upsert", (contacts) => void saveContacts(db, connectionId, contacts));
    sock.ev.on("contacts.update", (contacts) => void saveContacts(db, connectionId, contacts));
    sock.ev.on("call", (calls) => void onCalls(db, connectionId, calls));
    sock.ev.on("messages.update", (updates) => void onMessagesUpdate(db, connectionId, updates as never).catch((err) => logger.error({ err }, "messages.update failed")));
    sock.ev.on("message-receipt.update", (receipts) => void onReceipts(db, connectionId, receipts as never).catch((err) => logger.error({ err }, "receipt update failed")));
    sock.ev.on("lid-mapping.update", (m) => void saveLidMapping(db, connectionId, m.lid, m.pn).catch(() => undefined));
    sock.ev.on("messaging-history.set", (h) => void onHistory(db, connectionId, h as never).catch((err) => logger.error({ err }, "history sync failed")));
    sock.ev.on("chats.upsert", (chats) => void saveChatNames(db, connectionId, chats as never));
    sock.ev.on("chats.update", (chats) => void saveChatNames(db, connectionId, chats as never));
    sock.ev.on("presence.update", (p) => onPresence(connectionId, p as never));
    sock.ev.on("contacts.update", (contacts) => {
      // A changed or removed profile picture invalidates the cached copy.
      for (const c of contacts) if (c.id && c.imgUrl !== undefined) void forgetProfilePicture(db, connectionId, c.id);
    });
    sock.ev.on("connection.update", (u) => { if (u.connection === "open") void resyncNames(db, connectionId, entry); });
  })();

  try {
    await entry.starting;
  } finally {
    entry.starting = null;
  }
}

type CallLike = { id: string; from: string; chatId?: string; date: Date; isVideo?: boolean; isGroup?: boolean; status: string };

/** A call nobody picked up ("timeout") is recorded as a message-like entry so it shows in the chat thread and
 *  can raise a notification. Baileys only reports calls that reach this linked device while it's connected. */
async function onCalls(db: Db, connectionId: string, calls: CallLike[]): Promise<void> {
  for (const call of calls) {
    if (call.status !== "timeout" || call.isGroup) continue;
    const chatId = call.from;
    const text = call.isVideo ? "📹 Missed video call" : "📞 Missed voice call";
    const timestamp = call.date ?? new Date();
    try {
      const result = await messagesCollection(db).updateOne(
        { connectionId, messageId: `call-${call.id}` },
        { $setOnInsert: { connectionId, messageId: `call-${call.id}`, chatId, fromMe: false, isAiGenerated: false, text, reactions: [], timestamp, createdAt: new Date() } },
        { upsert: true },
      );
      if (result.upsertedCount === 0) continue; // already recorded
    } catch (err) {
      console.error(`Failed to record missed call for connection ${connectionId}:`, err);
      continue;
    }
    await reportEvent(connectionId, "WhatsAppMessageReceived", { messageId: `call-${call.id}`, chatId, preview: text, timestamp });
  }
}

const businessChecked = new Set<string>();

/** Asks WhatsApp whether a 1:1 contact has a business profile, once per contact per process. WhatsApp
 *  answers with the profile for business accounts and nothing (or an error) for personal ones. */
async function ensureBusinessFlag(db: Db, connectionId: string, chatId: string): Promise<void> {
  if (chatId.endsWith("@g.us") || chatId === "status@broadcast") return;
  const key = `${connectionId}:${chatId}`;
  if (businessChecked.has(key)) return;
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) return;
  businessChecked.add(key);
  try {
    const profile = await sock.getBusinessProfile(chatId);
    await contactsCollection(db).updateOne({ connectionId, contactId: chatId }, { $set: { isBusiness: Boolean(profile), updatedAt: new Date() } }, { upsert: true });
  } catch {
    /* not a business account, or WhatsApp declined the query — treated as a personal contact */
  }
}

const groupNamesSeen = new Set<string>();

/** A group message's pushName is the *sender's* name, not the group's — fetch the group subject once. */
async function ensureGroupName(db: Db, connectionId: string, chatId: string): Promise<void> {
  if (!chatId.endsWith("@g.us")) return;
  const key = `${connectionId}:${chatId}`;
  if (groupNamesSeen.has(key)) return;
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) return;
  try {
    const meta = await sock.groupMetadata(chatId);
    groupNamesSeen.add(key);
    await saveContacts(db, connectionId, [{ id: chatId, name: meta.subject }]);
  } catch {
    /* best-effort; retried on the group's next message */
  }
}

async function onConnectionUpdate(
  db: Db,
  connectionId: string,
  entry: SessionEntry,
  update: { connection?: "open" | "connecting" | "close"; lastDisconnect?: { error?: Error }; qr?: string },
): Promise<void> {
  const { connection, lastDisconnect, qr } = update;

  if (qr) {
    entry.qr = qr;
    entry.qrExpiresAt = new Date(Date.now() + QR_LIFETIME_MS);
    entry.status = "Connecting";
    await reportEvent(connectionId, "WhatsAppQrGenerated", { status: "Connecting", qr, qrExpiresAt: entry.qrExpiresAt });
    // Baileys doesn't emit a distinct "expired" event; schedule our own so UIs relying on this service's
    // GET /qr (rather than just the SignalR push) can tell a stale code apart from a fresh one.
    setTimeout(() => {
      if (entry.qr === qr && entry.status !== "Connected") void reportEvent(connectionId, "WhatsAppQrExpired", { status: entry.status });
    }, QR_LIFETIME_MS);
  }

  if (connection === "open") {
    entry.status = "Connected";
    entry.qr = undefined;
    entry.qrExpiresAt = undefined;
    entry.reconnectAttempts = 0;
    entry.accountIdentifier = entry.sock?.user?.id;
    entry.lastError = undefined;
    await reportEvent(connectionId, "WhatsAppConnected", { status: "Connected", accountIdentifier: entry.accountIdentifier });
  }

  if (connection === "close") {
    const statusCode = (lastDisconnect?.error as Boom | undefined)?.output?.statusCode;
    const loggedOut = statusCode === DisconnectReason.loggedOut;

    if (loggedOut) {
      entry.status = "Disconnected";
      entry.sock = null;
      await removeAllCredentials(db, connectionId);
      await reportEvent(connectionId, "WhatsAppDisconnected", { status: "Disconnected" });
      return;
    }

    // "Replaced" = another client is using this same WhatsApp login right now. Reconnecting just kicks it off and
    // gets kicked back, so neither side is ever stable. After a few in a row, stop and say so clearly.
    if (statusCode === DisconnectReason.connectionReplaced) {
      const now = Date.now();
      entry.replacedAt = [...(entry.replacedAt ?? []).filter((t) => now - t < REPLACED_WINDOW_MS), now];
      if (entry.replacedAt.length >= REPLACED_LIMIT) {
        entry.status = "Error";
        entry.sock = null;
        entry.lastError =
          "Another copy of Daksh (or another service) is using this WhatsApp login, so the two keep disconnecting each other. " +
          "Stop the other copy, then press Reconnect.";
        logger.error({ connectionId }, entry.lastError);
        await reportEvent(connectionId, "WhatsAppConnectionFailed", { status: "Error", error: entry.lastError });
        return;
      }
    }

    entry.reconnectAttempts += 1;
    if (entry.reconnectAttempts > MAX_RECONNECT_ATTEMPTS) {
      entry.status = "Error";
      entry.lastError = "Could not reconnect to WhatsApp after repeated attempts.";
      await reportEvent(connectionId, "WhatsAppConnectionFailed", { status: "Error", error: entry.lastError });
      return;
    }

    entry.status = "Connecting";
    const delayMs = Math.min(1000 * 2 ** entry.reconnectAttempts, 30_000);
    if (entry.reconnectTimer) clearTimeout(entry.reconnectTimer);
    entry.reconnectTimer = setTimeout(() => void startSocket(db, connectionId), delayMs);
  }
}

function extractMessageText(raw: WAMessage["message"] | null | undefined): string {
  // View-once, disappearing-message and caption wrappers hide the real content one level down.
  const message = normalizeMessageContent(raw);
  if (!message) return "";
  return (
    message.conversation ??
    message.extendedTextMessage?.text ??
    message.imageMessage?.caption ??
    message.videoMessage?.caption ??
    message.documentMessage?.caption ??
    (message.imageMessage ? "📷 Photo" : undefined) ??
    (message.videoMessage ? "🎥 Video" : undefined) ??
    (message.audioMessage ? "🎤 Voice message" : undefined) ??
    (message.documentMessage ? "📄 Document" : undefined) ??
    (message.locationMessage ? "📍 Location" : undefined) ??
    (message.stickerMessage ? "Sticker" : undefined) ??
    (message.contactMessage ? `👤 Contact: ${message.contactMessage.displayName ?? ""}`.trim() : undefined) ??
    (message.contactsArrayMessage ? `👤 Contacts: ${(message.contactsArrayMessage.contacts ?? []).map((c) => c.displayName).filter(Boolean).join(", ")}`.trim() : undefined) ??
    ""
  );
}

/** A reply-to quote lives in `contextInfo`, which hangs off whichever message-type field is actually set
 *  (extendedTextMessage, imageMessage, ...) — there's no single shared field for it. */
function extractContextInfo(raw: WAMessage["message"] | null | undefined) {
  const message = normalizeMessageContent(raw);
  if (!message) return undefined;
  return (
    message.extendedTextMessage?.contextInfo ??
    message.imageMessage?.contextInfo ??
    message.videoMessage?.contextInfo ??
    message.audioMessage?.contextInfo ??
    message.documentMessage?.contextInfo ??
    message.stickerMessage?.contextInfo ??
    undefined
  );
}

/** Upserts one message by (connectionId, messageId) — shared by the live incoming-message listener and by
 *  sendMessage(), since outgoing messages are captured from sendMessage's own return value, not this event
 *  (see the comment on sendMessage for why). Returns the stored doc, or null if this message had nothing
 *  displayable (receipts, reactions, other content-less protocol messages). */
async function persistMessage(
  db: Db,
  connectionId: string,
  m: WAMessage,
  text: string,
  isAiGenerated = false,
  skipMedia = false,
): Promise<{ chatId: string; chatName?: string; timestamp: Date } | null> {
  if (!m.key.id || !m.key.remoteJid || !text) return null;
  // A phone-synced copy of an AI reply carries the signature; store it clean and keep the AI flag.
  for (const sig of [AI_SIGNATURE, ...LEGACY_AI_SIGNATURES]) {
    if (text.endsWith(sig) && (m.key.fromMe ?? false)) {
      text = text.slice(0, -sig.length);
      isAiGenerated = true;
    }
  }
  if (m.key.remoteJid.endsWith("@broadcast") || m.key.remoteJid.endsWith("@newsletter")) return null;

  const fromMe = m.key.fromMe ?? false;
  const timestamp = m.messageTimestamp ? new Date(Number(m.messageTimestamp) * 1000) : new Date();
  const contextInfo = extractContextInfo(m.message);
  const media = detectMedia(m.message);
  const doc = {
    connectionId,
    messageId: m.key.id,
    chatId: m.key.remoteJid,
    // pushName is the *sender's* name: on our own messages that is us, so it must never become the chat's name.
    chatName: fromMe ? undefined : (m.pushName ?? undefined),
    participant: m.key.participant ?? undefined,
    fromMe,
    isAiGenerated: fromMe && isAiGenerated,
    text,
    quotedMessageId: contextInfo?.stanzaId ?? undefined,
    quotedText: contextInfo?.quotedMessage ? extractMessageText(contextInfo.quotedMessage) : undefined,
    ...(media ? { mediaKind: media.kind, mimeType: media.mimeType, fileName: media.fileName, mediaSeconds: media.seconds, voiceNote: media.voiceNote } : {}),
    ...(fromMe ? { status: STATUS.pending } : {}),
    reactions: [],
    timestamp,
    createdAt: new Date(),
  };

  try {
    await messagesCollection(db).updateOne({ connectionId, messageId: doc.messageId }, { $setOnInsert: doc }, { upsert: true });
  } catch (err) {
    console.error(`Failed to persist WhatsApp message for connection ${connectionId}:`, err);
  }
  void ensureGroupName(db, connectionId, doc.chatId);
  if (!skipMedia) await storeMedia(db, connectionId, m);
  return { chatId: doc.chatId, chatName: doc.chatName, timestamp };
}

/** Appended (in WhatsApp italics) to every reply Daksha's AI sends, so the recipient knows it's automated.
 *  Stripped again before storing — Daksha's own UI shows it as a caption instead. */
const AI_SIGNATURE = "\n\n_🤖 Reply by AI Assistant_";
const LEGACY_AI_SIGNATURES = ["\n\n_Reply by AI Assistant_", "\n\n_ʀᴇᴘʟʏ ʙʏ ᴀɪ ᴀꜱꜱɪꜱᴛᴀɴᴛ_"];

/** A reaction arrives as its own "message" whose `reactionMessage.key` points at the message being
 *  reacted to (not this event's own key) — handled separately from persistMessage, which is for actual
 *  chat messages. An empty `text` means the sender removed their reaction. */
async function onReaction(db: Db, connectionId: string, m: WAMessage): Promise<void> {
  const reaction = m.message?.reactionMessage;
  const targetId = reaction?.key?.id;
  if (!targetId || !m.key.remoteJid) return;

  const by = m.key.fromMe ? "me" : m.key.participant ?? m.key.remoteJid;
  const messages = messagesCollection(db);
  await messages.updateOne({ connectionId, messageId: targetId }, { $pull: { reactions: { by } } });
  if (reaction?.text) await messages.updateOne({ connectionId, messageId: targetId }, { $push: { reactions: { by, emoji: reaction.text } } });

  await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: targetId, chatId: m.key.remoteJid });
}

/** Persists every live (non-history-sync) message and reports it so the .NET API can notify the owning
 *  user or sync an open chat view. Baileys replays old history through this same event with type
 *  "append"/"notify" during an initial sync — only "notify" is an actual live arrival, so history replay
 *  never fires anything.
 *
 *  A message sent through sendMessage() here is captured from its own return value instead (Baileys
 *  doesn't reliably refire this event for something the same socket just sent) — but a message sent from
 *  the *phone itself*, or another linked device, never goes through sendMessage() and only ever arrives
 *  here, with fromMe: true. persistMessage()'s upsert-by-messageId makes re-processing either kind
 *  idempotent, so there's no double-counting when both paths do see the same message. */
async function onMessagesUpsert(
  db: Db,
  connectionId: string,
  payload: { messages: WAMessage[]; type: "notify" | "append" },
): Promise<void> {
  if (payload.type !== "notify") return;

  for (const m of payload.messages) {
    // Status updates ("stories") and broadcast/newsletter posts aren't conversations — never store or notify.
    const jid = m.key.remoteJid ?? "";
    if (jid === "status@broadcast" || jid.endsWith("@broadcast") || jid.endsWith("@newsletter")) continue;
    if (await handleProtocolMessage(db, connectionId, m)) continue; // "deleted for everyone" / "edited"
    if (m.message?.reactionMessage) {
      await onReaction(db, connectionId, m);
      continue;
    }
    const text = extractMessageText(m.message);
    const stored = await persistMessage(db, connectionId, m, text);
    if (!stored) continue;
    // Awaited before reporting so the API can tell a business contact apart on the very first message.
    if (!m.key.fromMe) await ensureBusinessFlag(db, connectionId, stored.chatId);

    if (m.key.fromMe) {
      // Sent from the phone or another linked device — still worth syncing to an open chat thread/list.
      await reportEvent(connectionId, "WhatsAppMessageSent", {
        messageId: m.key.id!,
        chatId: stored.chatId,
        chatName: stored.chatName,
        preview: text,
        timestamp: stored.timestamp,
      });
      continue;
    }

    // Full text, not a truncated preview: the .NET side needs the whole message to generate an AI
    // auto-reply from it, and truncates for display itself where that's all it needs.
    await reportEvent(connectionId, "WhatsAppMessageReceived", {
      messageId: m.key.id!,
      chatId: stored.chatId,
      chatName: stored.chatName,
      preview: text,
      timestamp: stored.timestamp,
    });
  }
}

/** Starts (or resumes, idempotently) a session for this connectionId. */
export async function connect(db: Db, connectionId: string): Promise<void> {
  await startSocket(db, connectionId);
}

export function getQr(connectionId: string): { qr: string | null; expiresAt: Date | null; status: ConnectionStatus } {
  const entry = sessions.get(connectionId);
  if (!entry) return { qr: null, expiresAt: null, status: "Disconnected" };
  const expired = entry.qrExpiresAt ? entry.qrExpiresAt.getTime() < Date.now() : false;
  return { qr: expired ? null : (entry.qr ?? null), expiresAt: entry.qrExpiresAt ?? null, status: entry.status };
}

export function getStatus(connectionId: string): { status: ConnectionStatus; accountIdentifier?: string; lastError?: string } {
  const entry = sessions.get(connectionId);
  if (!entry) return { status: "Disconnected" };
  return { status: entry.status, accountIdentifier: entry.accountIdentifier, lastError: entry.lastError };
}

export async function reconnect(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  entry.reconnectAttempts = 0;
  entry.replacedAt = [];
  if (entry.reconnectTimer) clearTimeout(entry.reconnectTimer);
  if (entry.sock) {
    try {
      entry.sock.end(undefined);
    } catch {
      /* already closed */
    }
    entry.sock = null;
  }
  await startSocket(db, connectionId);
}

/** Sends a text message on a live session, to an existing chat or a brand-new contact JID alike, optionally
 *  quoting an earlier message by id (the "reply" swipe gesture WhatsApp itself has).
 *  Persists the sent message itself from sock.sendMessage()'s own return value — Baileys does not
 *  reliably refire "messages.upsert" for a message the same socket just sent, so relying on that event
 *  (as onMessagesUpsert does for incoming messages) silently drops every outgoing message. */
export async function sendMessage(
  db: Db,
  connectionId: string,
  chatId: string,
  text: string,
  quotedMessageId?: string,
  isAiGenerated = false,
  signature?: string,
): Promise<void> {
  const entry = sessions.get(connectionId);
  if (!entry?.sock || entry.status !== "Connected") throw new Error("WhatsApp is not connected.");

  let quoted: WAMessage | undefined;
  if (quotedMessageId) {
    const quotedDoc = await messagesCollection(db).findOne({ connectionId, messageId: quotedMessageId });
    if (quotedDoc) {
      // Baileys only needs enough of the original to render the quote preview and thread the reply — the
      // full original proto isn't required, and we don't keep it (only its extracted text).
      quoted = {
        key: { remoteJid: quotedDoc.chatId, id: quotedDoc.messageId, fromMe: quotedDoc.fromMe, participant: quotedDoc.participant },
        message: { conversation: quotedDoc.text },
      } as WAMessage;
    }
  }

  const wireText = isAiGenerated && signature ? `${text}\n\n_${signature}_` : text;
  const sent = await entry.sock.sendMessage(chatId, { text: wireText }, quoted ? { quoted } : undefined);
  if (!sent) return;
  const stored = await persistMessage(db, connectionId, sent, text, isAiGenerated);
  if (!stored) return;
  // Without this, an open chat thread (or the chat list) never learns a reply/auto-reply went out — only
  // incoming messages report an event, so anything you (or Daksha) send just sits there until a refetch.
  await reportEvent(connectionId, "WhatsAppMessageSent", {
    messageId: sent.key.id ?? undefined,
    chatId: stored.chatId,
    chatName: stored.chatName,
    preview: text,
    timestamp: stored.timestamp,
  });
}

/** Reacts to an existing message with an emoji (or removes a prior reaction by passing ""), the same way
 *  Baileys reports others' reactions back to us (see onReaction) — reflected locally right away since,
 *  like sent messages, our own reactions aren't reliably refired through messages.upsert either. */
export async function sendReaction(db: Db, connectionId: string, chatId: string, messageId: string, emoji: string): Promise<void> {
  const entry = sessions.get(connectionId);
  if (!entry?.sock || entry.status !== "Connected") throw new Error("WhatsApp is not connected.");

  const target = await messagesCollection(db).findOne({ connectionId, messageId });
  if (!target) throw new Error("Message not found.");

  const key = { remoteJid: target.chatId, id: target.messageId, fromMe: target.fromMe, participant: target.participant };
  await entry.sock.sendMessage(chatId, { react: { text: emoji, key } });

  const messages = messagesCollection(db);
  await messages.updateOne({ connectionId, messageId }, { $pull: { reactions: { by: "me" } } });
  if (emoji) await messages.updateOne({ connectionId, messageId }, { $push: { reactions: { by: "me", emoji } } });
  await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId, chatId });
}

/** Logs the WhatsApp account out (invalidating the session on WhatsApp's side) and clears stored credentials. */
export async function disconnect(db: Db, connectionId: string): Promise<void> {
  const entry = sessions.get(connectionId);
  if (entry?.reconnectTimer) clearTimeout(entry.reconnectTimer);
  if (entry?.sock) {
    try {
      await entry.sock.logout();
    } catch {
      /* best-effort; session is being torn down either way */
    }
  }
  await removeAllCredentials(db, connectionId);
  sessions.delete(connectionId);
  await reportEvent(connectionId, "WhatsAppDisconnected", { status: "Disconnected" });
}

/** Called once at startup: resumes every connection this process previously held an active WhatsApp session for. */
export async function restoreSessionsFromDatabase(db: Db): Promise<void> {
  const connectionIds = await db.collection("integrationCredentials").distinct("connectionId", { credentialKey: "creds" });
  await Promise.all(
    connectionIds.map(async (connectionId: string) => {
      try {
        await connect(db, connectionId);
      } catch (err) {
        logger.error({ err, connectionId }, "Failed to restore WhatsApp session on startup");
      }
    }),
  );
}

// ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// More of WhatsApp: history, names, presence, media, read receipts, delete/edit, forward, groups, profile pictures
// ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

type HistoryLike = { contacts?: Parameters<typeof saveContacts>[2]; lidPnMappings?: { lid: string; pn: string }[]; chats?: { id?: string; name?: string | null }[]; messages?: WAMessage[] };

/** What WhatsApp hands over when a device is linked: contacts (with address-book names), lid mappings and recent messages. */
async function onHistory(db: Db, connectionId: string, h: HistoryLike): Promise<void> {
  if (h.contacts?.length) await saveContacts(db, connectionId, h.contacts);
  for (const m of h.lidPnMappings ?? []) await saveLidMapping(db, connectionId, m.lid, m.pn);
  if (h.chats?.length) await saveChatNames(db, connectionId, h.chats);
  // Old messages are stored quietly (no notifications, no auto-replies, no media downloads), newest first, capped.
  let stored = 0;
  for (const m of h.messages ?? []) {
    if (stored >= 3000) break;
    const jid = m.key.remoteJid ?? "";
    if (!jid || jid === "status@broadcast" || jid.endsWith("@broadcast") || jid.endsWith("@newsletter")) continue;
    if (normalizeMessageContent(m.message)?.protocolMessage || m.message?.reactionMessage) continue;
    const text = extractMessageText(m.message);
    if (await persistMessage(db, connectionId, m, text, false, true)) stored++;
  }
}

/** Group subjects arrive as chat names; a one-to-one chat's `name` may be a self-chosen display name, so it is not saved as an address-book name. */
async function saveChatNames(db: Db, connectionId: string, chats: { id?: string | null; name?: string | null }[]): Promise<void> {
  const groups = chats.filter((c) => c.id?.endsWith("@g.us") && c.name).map((c) => ({ id: c.id as string, name: c.name as string }));
  if (groups.length) await saveContacts(db, connectionId, groups);
}

/** Asks WhatsApp to re-send the phone's address book (the names you saved), at most once every 12 hours. */
async function resyncNames(db: Db, connectionId: string, entry: SessionEntry): Promise<void> {
  try {
    const state = db.collection("whatsappServiceState");
    const doc = await state.findOne({ connectionId });
    if (doc?.lastNameSync && Date.now() - new Date(doc.lastNameSync as Date).getTime() < 12 * 3600 * 1000) return;
    if (!entry.sock) return;
    // WhatsApp only re-sends what changed since the last sync, so the saved names (the "contact" collection) would never
    // arrive again. Forgetting the stored version makes it send the whole address book once.
    await entry.sock.authState.keys.set({ "app-state-sync-version": { critical_unblock_low: null } } as never);
    await entry.sock.resyncAppState(["critical_unblock_low"], true);
    await state.updateOne({ connectionId }, { $set: { connectionId, lastNameSync: new Date() } }, { upsert: true });
  } catch (err) {
    logger.warn({ err }, "Could not resync the address book names");
  }
}

// ── Typing / online ───────────────────────────────────────────────────────────────────────────────────────────────
type PresenceKind = "composing" | "recording" | "available" | "unavailable" | "paused";
const presenceByChat = new Map<string, { state: PresenceKind; lastSeen?: number; at: number }>();

function onPresence(connectionId: string, p: { id: string; presences?: Record<string, { lastKnownPresence?: PresenceKind; lastSeen?: number }> }): void {
  const all = Object.values(p.presences ?? {});
  const active = all.find((v) => v.lastKnownPresence === "composing" || v.lastKnownPresence === "recording") ?? all[0];
  if (active?.lastKnownPresence) presenceByChat.set(`${connectionId}:${p.id}`, { state: active.lastKnownPresence, lastSeen: active.lastSeen, at: Date.now() });
}

/** Subscribes to the chat's presence (WhatsApp only reports it to subscribers) and returns the latest known state. */
export async function getPresence(connectionId: string, chatId: string): Promise<{ state: PresenceKind | "unknown"; lastSeen?: number }> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock || chatId.endsWith("@broadcast")) return { state: "unknown" };
  const key = `${connectionId}:${chatId}`;
  const known = presenceByChat.get(key);
  if (!known) void sock.presenceSubscribe(chatId).catch(() => undefined);
  if (!known) return { state: "unknown" };
  // "typing…" is only believable for a few seconds after it was reported.
  if ((known.state === "composing" || known.state === "recording") && Date.now() - known.at > 12_000) return { state: "available", lastSeen: known.lastSeen };
  return { state: known.state, lastSeen: known.lastSeen };
}

/** Tells the other person we are typing (or stopped). */
export async function setPresence(connectionId: string, chatId: string, state: "composing" | "paused" | "available"): Promise<void> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  await sock.sendPresenceUpdate(state, chatId);
}

// ── Sending media ─────────────────────────────────────────────────────────────────────────────────────────────────
async function quotedFor(db: Db, connectionId: string, quotedMessageId?: string): Promise<WAMessage | undefined> {
  if (!quotedMessageId) return undefined;
  const q = await messagesCollection(db).findOne({ connectionId, messageId: quotedMessageId });
  if (!q) return undefined;
  return { key: { remoteJid: q.chatId, id: q.messageId, fromMe: q.fromMe, participant: q.participant }, message: { conversation: q.text } } as WAMessage;
}

export type OutgoingMedia = { kind: MediaKind; data: Buffer; mimeType: string; fileName?: string; caption?: string; quotedMessageId?: string; voiceNote?: boolean };

/** Sends a photo, video, voice note, file or sticker, and keeps a copy so the thread can show it straight away. */
export async function sendMedia(db: Db, connectionId: string, chatId: string, media: OutgoingMedia): Promise<void> {
  const entry = sessions.get(connectionId);
  if (!entry?.sock || entry.status !== "Connected") throw new Error("WhatsApp is not connected.");
  if (media.data.length === 0) throw new Error("The file is empty.");
  if (media.data.length > mediaLimit(media.kind)) throw new Error(`That file is too large to send (limit ${Math.round(mediaLimit(media.kind) / 1048576)} MB).`);
  const quoted = await quotedFor(db, connectionId, media.quotedMessageId);
  const { kind, data, mimeType, fileName, caption } = media;
  const content =
    kind === "image" ? { image: data, caption, mimetype: mimeType }
    : kind === "video" ? { video: data, caption, mimetype: mimeType }
    : kind === "audio" ? { audio: data, mimetype: mimeType, ptt: Boolean(media.voiceNote) }
    : kind === "sticker" ? { sticker: data }
    : { document: data, mimetype: mimeType, fileName: fileName ?? "file", caption };
  const sent = await entry.sock.sendMessage(chatId, content as never, quoted ? { quoted } : undefined);
  if (!sent) return;
  const label = caption || (kind === "image" ? "📷 Photo" : kind === "video" ? "🎥 Video" : kind === "audio" ? "🎤 Voice message" : kind === "sticker" ? "Sticker" : "📄 Document");
  const stored = await persistMessage(db, connectionId, sent, label, false, true);
  if (!stored) return;
  await saveMediaBytes(db, connectionId, sent.key.id!, data, mimeType);
  await messagesCollection(db).updateOne({ connectionId, messageId: sent.key.id! }, { $set: { hasMedia: true, mediaKind: kind, mimeType, ...(fileName ? { fileName } : {}) } });
  await reportEvent(connectionId, "WhatsAppMessageSent", { messageId: sent.key.id!, chatId: stored.chatId, chatName: stored.chatName, preview: label, timestamp: stored.timestamp });
}

// ── Read receipts, delete, edit, forward ──────────────────────────────────────────────────────────────────────────
/** Tells WhatsApp the incoming messages in this chat were read (the blue ticks on the sender's side). Only called when a person opens the chat. */
export async function markChatRead(db: Db, connectionId: string, chatId: string): Promise<number> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  const unread = await messagesCollection(db).find({ connectionId, chatId, fromMe: false, readSent: { $ne: true }, deleted: { $ne: true } }).sort({ timestamp: -1 }).limit(100).toArray();
  if (unread.length === 0) return 0;
  await sock.readMessages(unread.map((m) => ({ remoteJid: m.chatId, id: m.messageId, fromMe: false, participant: m.participant })));
  await messagesCollection(db).updateMany({ connectionId, messageId: { $in: unread.map((m) => m.messageId) } }, { $set: { readSent: true } });
  return unread.length;
}

/** Delete for everyone (WhatsApp removes it for the other person too) or delete for me (hidden in Daksha only). */
export async function deleteMessage(db: Db, connectionId: string, chatId: string, messageId: string, forEveryone: boolean): Promise<void> {
  const doc = await messagesCollection(db).findOne({ connectionId, messageId });
  if (!doc) throw new Error("Message not found.");
  if (forEveryone) {
    const sock = sessions.get(connectionId)?.sock;
    if (!sock) throw new Error("WhatsApp is not connected.");
    if (!doc.fromMe) throw new Error("You can only delete your own messages for everyone.");
    await sock.sendMessage(chatId, { delete: { remoteJid: doc.chatId, id: doc.messageId, fromMe: true, participant: doc.participant } });
    await markRevoked(db, connectionId, messageId);
  } else {
    await messagesCollection(db).updateOne({ connectionId, messageId }, { $set: { hidden: true } });
  }
  await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId, chatId });
}

/** WhatsApp lets you edit a message for 15 minutes after sending it. */
export async function editMessage(db: Db, connectionId: string, chatId: string, messageId: string, text: string): Promise<void> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  const doc = await messagesCollection(db).findOne({ connectionId, messageId });
  if (!doc || !doc.fromMe || doc.deleted) throw new Error("Only your own messages can be edited.");
  if (doc.mediaKind && doc.mediaKind !== "image" && doc.mediaKind !== "video" && doc.mediaKind !== "document") throw new Error("This kind of message cannot be edited.");
  if (Date.now() - doc.timestamp.getTime() > 15 * 60 * 1000) throw new Error("Messages can only be edited within 15 minutes of sending.");
  if (!text.trim()) throw new Error("The message cannot be empty.");
  await sock.sendMessage(chatId, { text, edit: { remoteJid: doc.chatId, id: doc.messageId, fromMe: true, participant: doc.participant } } as never);
  await applyEdit(db, connectionId, messageId, text);
  await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId, chatId });
}

/** Sends an existing message (text or its attachment) to another chat. */
export async function forwardMessage(db: Db, connectionId: string, toChatId: string, messageId: string): Promise<void> {
  const doc = await messagesCollection(db).findOne({ connectionId, messageId });
  if (!doc || doc.deleted) throw new Error("Message not found.");
  if (doc.hasMedia && doc.mediaKind) {
    const media = await db.collection("whatsappMedia").findOne({ connectionId, messageId });
    const bytes = media?.data as { buffer?: Buffer } | Buffer | undefined;
    const data = Buffer.isBuffer(bytes) ? bytes : bytes?.buffer;
    if (!data) throw new Error("The file is no longer available to forward.");
    const caption = doc.text && !["📷 Photo", "🎥 Video", "🎤 Voice message", "📄 Document", "Sticker"].includes(doc.text) ? doc.text : undefined;
    await sendMedia(db, connectionId, toChatId, { kind: doc.mediaKind, data, mimeType: doc.mimeType ?? "application/octet-stream", fileName: doc.fileName, caption, voiceNote: doc.voiceNote });
    return;
  }
  await sendMessage(db, connectionId, toChatId, doc.text);
}

// ── Groups and profile pictures ───────────────────────────────────────────────────────────────────────────────────
export async function groupInfo(connectionId: string, chatId: string) {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  const meta = await sock.groupMetadata(chatId);
  return {
    subject: meta.subject,
    description: meta.desc ?? null,
    owner: meta.owner ?? null,
    createdAt: meta.creation ? new Date(Number(meta.creation) * 1000).toISOString() : null,
    participants: meta.participants.map((p) => ({ id: p.id, phoneNumber: (p as { phoneNumber?: string }).phoneNumber ?? null, admin: p.admin ?? null })),
  };
}

export async function profilePicture(db: Db, connectionId: string, chatId: string) {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) return null;
  return getProfilePicture(db, sock, connectionId, chatId);
}

// ── Group admin, disappearing messages, block ─────────────────────────────────────────────────────────────────────────
export type GroupAction = "rename" | "description" | "invite" | "leave" | "add" | "remove" | "promote" | "demote";

/** Admin actions on a group. Only works when this account is an admin (WhatsApp refuses otherwise, and that message is passed on). */
export async function groupAction(connectionId: string, chatId: string, action: GroupAction, opts: { text?: string; participants?: string[] }): Promise<{ link?: string }> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  if (!chatId.endsWith("@g.us")) throw new Error("That chat is not a group.");
  const people = opts.participants ?? [];
  try { return await runGroupAction(sock, chatId, action, opts.text, people); }
  catch (err) {
    const m = err instanceof Error ? err.message : "";
    if (/not-authorized|forbidden|403/i.test(m)) throw new Error("Only group admins can do that.");
    throw err;
  }
}

async function runGroupAction(sock: WASocket, chatId: string, action: GroupAction, text: string | undefined, people: string[]): Promise<{ link?: string }> {
  const opts = { text };
  switch (action) {
    case "rename": if (!opts.text?.trim()) throw new Error("Enter a group name."); await sock.groupUpdateSubject(chatId, opts.text.trim()); return {};
    case "description": await sock.groupUpdateDescription(chatId, opts.text?.trim() || undefined); return {};
    case "invite": { const code = await sock.groupInviteCode(chatId); return { link: code ? `https://chat.whatsapp.com/${code}` : undefined }; }
    case "leave": await sock.groupLeave(chatId); return {};
    case "add": case "remove": case "promote": case "demote":
      if (people.length === 0) throw new Error("Choose who to change.");
      await sock.groupParticipantsUpdate(chatId, people, action);
      return {};
  }
}

/** Turns disappearing messages on for a chat (seconds) or off (0). */
export async function setDisappearing(connectionId: string, chatId: string, seconds: number): Promise<void> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  await sock.sendMessage(chatId, { disappearingMessagesInChat: seconds > 0 ? seconds : false } as never);
}

export async function setBlocked(connectionId: string, chatId: string, blocked: boolean): Promise<void> {
  const sock = sessions.get(connectionId)?.sock;
  if (!sock) throw new Error("WhatsApp is not connected.");
  if (chatId.endsWith("@g.us")) throw new Error("A group cannot be blocked. Leave it instead.");
  await sock.updateBlockStatus(chatId, blocked ? "block" : "unblock");
}
