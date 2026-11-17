import type { Boom } from "@hapi/boom";
import type { Db } from "mongodb";
import { pino } from "pino";
import {
  DisconnectReason,
  fetchLatestBaileysVersion,
  makeCacheableSignalKeyStore,
  downloadMediaMessage,
  makeWASocket,
  type WAMessage,
  type WASocket,
} from "@whiskeysockets/baileys";
import { removeAllCredentials, useMongoAuthState } from "./authStateMongo.js";
import { contactsCollection, messagesCollection } from "./db.js";
import { reportEvent } from "./apiClient.js";

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
  })();

  try {
    await entry.starting;
  } finally {
    entry.starting = null;
  }
}

type ContactLike = { id?: string; lid?: string; name?: string; notify?: string; verifiedName?: string };

/** Stores address-book names / display names so the UI can show a person instead of a raw WhatsApp id.
 *  Partial updates only set the fields they actually carry, so a name-less update never erases a saved name. */
async function saveContacts(db: Db, connectionId: string, contacts: ContactLike[]): Promise<void> {
  for (const c of contacts) {
    if (!c.id) continue;
    const set: Record<string, unknown> = { updatedAt: new Date() };
    if (c.lid) set.lid = c.lid;
    if (c.name) set.name = c.name;
    const notify = c.notify ?? c.verifiedName;
    if (notify) set.notify = notify;
    if (c.verifiedName) set.isBusiness = true; // a verified name only exists on business accounts
    try {
      await contactsCollection(db).updateOne({ connectionId, contactId: c.id }, { $set: set }, { upsert: true });
    } catch (err) {
      console.error(`Failed to save WhatsApp contact for connection ${connectionId}:`, err);
    }
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

function extractMessageText(message: WAMessage["message"] | null | undefined): string {
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
function extractContextInfo(message: WAMessage["message"] | null | undefined) {
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
  const doc = {
    connectionId,
    messageId: m.key.id,
    chatId: m.key.remoteJid,
    chatName: m.pushName ?? undefined,
    participant: m.key.participant ?? undefined,
    fromMe,
    isAiGenerated: fromMe && isAiGenerated,
    text,
    quotedMessageId: contextInfo?.stanzaId ?? undefined,
    quotedText: contextInfo?.quotedMessage ? extractMessageText(contextInfo.quotedMessage) : undefined,
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
  await storeImage(db, connectionId, m);
  return { chatId: doc.chatId, chatName: doc.chatName, timestamp };
}

/** Appended (in WhatsApp italics) to every reply Daksha's AI sends, so the recipient knows it's automated.
 *  Stripped again before storing — Daksha's own UI shows it as a caption instead. */
const AI_SIGNATURE = "\n\n_🤖 Reply by AI Assistant_";
const LEGACY_AI_SIGNATURES = ["\n\n_Reply by AI Assistant_", "\n\n_ʀᴇᴘʟʏ ʙʏ ᴀɪ ᴀꜱꜱɪꜱᴛᴀɴᴛ_"];

const MAX_IMAGE_BYTES = 8 * 1024 * 1024;

/** Photos and stickers are downloaded once and kept in "whatsappMedia" so the chat can render them; the
 *  message document gets `hasMedia` only after the bytes are safely stored. Best-effort: a failed download
 *  leaves the "📷 Photo" text label. */
async function storeImage(db: Db, connectionId: string, m: WAMessage): Promise<void> {
  const img = m.message?.imageMessage ?? m.message?.stickerMessage;
  if (!img || !m.key.id) return;
  try {
    const existing = await db.collection("whatsappMedia").findOne({ connectionId, messageId: m.key.id }, { projection: { _id: 1 } });
    if (!existing) {
      const buffer = (await downloadMediaMessage(m, "buffer", {})) as Buffer;
      if (buffer.length === 0 || buffer.length > MAX_IMAGE_BYTES) return;
      await db.collection("whatsappMedia").updateOne(
        { connectionId, messageId: m.key.id },
        { $setOnInsert: { connectionId, messageId: m.key.id, mimeType: img.mimetype ?? "image/jpeg", data: buffer, createdAt: new Date() } },
        { upsert: true },
      );
    }
    await messagesCollection(db).updateOne({ connectionId, messageId: m.key.id }, { $set: { hasMedia: true } });
  } catch (err) {
    console.error(`Failed to download WhatsApp image for connection ${connectionId}:`, err);
  }
}

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
