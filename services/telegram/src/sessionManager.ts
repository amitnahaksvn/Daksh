import type { Db } from "mongodb";
import bigInt from "big-integer";
import { Api, TelegramClient } from "telegram";
import { StringSession } from "telegram/sessions/index.js";
import { NewMessage, type NewMessageEvent } from "telegram/events/index.js";
import { Raw } from "telegram/events/Raw.js";
import { CustomFile } from "telegram/client/uploads.js";
import { STATUS, chatKey, onUpdate, presenceOf } from "./extras.js";
import { reportEvent } from "./apiClient.js";
import { config, telegramConfigured } from "./config.js";
import { decrypt, encrypt } from "./crypto.js";
import { credentialsCollection } from "./db.js";

type Status = "NotConnected" | "Connecting" | "Connected" | "Disconnected" | "Error";

type Entry = {
  client?: TelegramClient;
  status: Status;
  qr?: string;
  qrExpiresAt?: Date;
  /** The account has two-step verification on: the user must supply their cloud password. */
  passwordRequired: boolean;
  passwordResolver?: (password: string) => void;
  error?: string;
};

const SESSION_KEY = "telegramSession";
const sessions = new Map<string, Entry>();

function entryFor(connectionId: string): Entry {
  let entry = sessions.get(connectionId);
  if (!entry) {
    entry = { status: "NotConnected", passwordRequired: false };
    sessions.set(connectionId, entry);
  }
  return entry;
}

export function getState(connectionId: string) {
  const entry = sessions.get(connectionId);
  if (!entry) return { status: "Disconnected" as Status, qr: null, expiresAt: null, passwordRequired: false, error: null };
  const expired = entry.qrExpiresAt ? entry.qrExpiresAt.getTime() < Date.now() : false;
  return {
    status: entry.status,
    qr: expired ? null : (entry.qr ?? null),
    expiresAt: entry.qrExpiresAt?.toISOString() ?? null,
    passwordRequired: entry.passwordRequired,
    error: entry.error ?? null,
  };
}

async function saveSession(db: Db, connectionId: string, client: TelegramClient): Promise<void> {
  const encryptedValue = encrypt(String(client.session.save()));
  await credentialsCollection(db).updateOne(
    { connectionId, credentialKey: SESSION_KEY },
    { $set: { encryptedValue, encryptionVersion: "aes-256-gcm-v1", updatedAt: new Date() }, $setOnInsert: { createdAt: new Date() } },
    { upsert: true },
  );
}

async function loadSession(db: Db, connectionId: string): Promise<string | undefined> {
  const doc = await credentialsCollection(db).findOne({ connectionId, credentialKey: SESSION_KEY });
  return doc ? decrypt(doc.encryptedValue) : undefined;
}

function newClient(session: string): TelegramClient {
  const client = new TelegramClient(new StringSession(session), config.apiId, config.apiHash, { connectionRetries: 5, useWSS: false });
  client.setLogLevel("none" as never);
  return client;
}

function nameOf(entity: unknown): string | undefined {
  if (entity instanceof Api.User) return [entity.firstName, entity.lastName].filter(Boolean).join(" ") || entity.username || undefined;
  if (entity instanceof Api.Chat || entity instanceof Api.Channel) return entity.title;
  return undefined;
}

/** Broadcast channels are one-way feeds — not conversations Daksha should list or answer. */
function isBroadcast(entity: unknown): boolean {
  return entity instanceof Api.Channel && entity.broadcast === true;
}

const MAX_PHOTO_BYTES = 8 * 1024 * 1024;
const MAX_FILE_BYTES = 14 * 1024 * 1024;

export type MediaKind = "photo" | "video" | "audio" | "voice" | "document" | "sticker";

/** What kind of attachment a message carries (known even when its bytes are not downloaded). */
function mediaInfo(msg: Api.Message): { kind: MediaKind; mimeType: string; fileName?: string; size: number } | undefined {
  const size = Number(msg.file?.size ?? 0);
  const mime = msg.file?.mimeType;
  if (msg.photo) return { kind: "photo", mimeType: "image/jpeg", size };
  if (msg.sticker) return { kind: "sticker", mimeType: mime ?? "image/webp", size };
  if (msg.voice) return { kind: "voice", mimeType: mime ?? "audio/ogg", size };
  if (msg.audio) return { kind: "audio", mimeType: mime ?? "audio/mpeg", fileName: msg.file?.name ?? undefined, size };
  if (msg.video || msg.videoNote || msg.gif) return { kind: "video", mimeType: mime ?? "video/mp4", size };
  if (msg.document) return { kind: "document", mimeType: mime ?? "application/octet-stream", fileName: msg.file?.name ?? undefined, size };
  return undefined;
}

/** What a message without text is shown as, so attachments still appear in the thread. */
function mediaLabel(msg: Api.Message): string | undefined {
  const k = mediaInfo(msg)?.kind;
  return k === "photo" ? "📷 Photo" : k === "sticker" ? "Sticker" : k === "voice" ? "🎤 Voice message" : k === "audio" ? "🎵 Audio" : k === "video" ? "🎥 Video" : k === "document" ? "📄 Document" : undefined;
}

/** Downloads the attachment once and keeps it in "telegramMedia" so the thread can show or play it. Best-effort. */
async function storeMedia(db: Db, connectionId: string, msg: Api.Message): Promise<boolean> {
  const info = mediaInfo(msg);
  if (!info || !msg.id || msg.chatId === undefined) return false;
  if (info.mimeType === "application/x-tgsticker") return false; // animated stickers are not viewable as an image
  const limit = info.kind === "photo" || info.kind === "sticker" ? MAX_PHOTO_BYTES : MAX_FILE_BYTES;
  if (info.size > limit) return false;
  const key = { connectionId, chatId: String(msg.chatId), messageId: String(msg.id) };
  try {
    if (await db.collection("telegramMedia").findOne(key, { projection: { _id: 1 } })) return true;
    const data = (await msg.downloadMedia({})) as Buffer | string | undefined;
    if (!Buffer.isBuffer(data) || data.length === 0 || data.length > limit) return false;
    await db.collection("telegramMedia").updateOne(key, { $setOnInsert: { ...key, mimeType: info.mimeType, fileName: info.fileName, data, createdAt: new Date() } }, { upsert: true });
    return true;
  } catch (err) {
    console.warn(`Could not download a Telegram ${info.kind} for ${connectionId}: ${(err as Error).message}`);
    return false;
  }
}

async function reportMessage(db: Db, connectionId: string, msg: Api.Message, history: boolean, mediaBudget = { left: Infinity }): Promise<void> {
  const text = msg.message || mediaLabel(msg);
  if (!text || !msg.id || msg.chatId === undefined) return;
  const chat = await msg.getChat().catch(() => undefined);
  if (isBroadcast(chat)) return;
  const sender = msg.out ? undefined : await msg.getSender().catch(() => undefined);
  const info = mediaInfo(msg);
  // Attachments are downloaded before the event goes out, so the thread already has them when it learns about the message.
  let hasMedia = false;
  if (info && mediaBudget.left > 0) { mediaBudget.left--; hasMedia = await storeMedia(db, connectionId, msg); }
  await reportEvent({
    connectionId,
    eventType: "Message",
    messageId: String(msg.id),
    chatId: String(msg.chatId),
    chatName: nameOf(chat),
    isGroup: !(chat instanceof Api.User),
    fromMe: msg.out === true,
    senderName: msg.out ? "Me" : nameOf(sender),
    text,
    timestamp: new Date(msg.date * 1000).toISOString(),
    history,
    hasMedia,
    mediaKind: info?.kind,
    mimeType: info?.mimeType,
    fileName: info?.fileName,
    replyToId: msg.replyToMsgId ? String(msg.replyToMsgId) : undefined,
  });
}

/** Seeds the chat list: pulls the most recent conversations so the screen isn't empty after connecting. It
 *  also warms GramJS's entity cache, which sending later relies on. */
async function backfill(db: Db, connectionId: string, client: TelegramClient): Promise<void> {
  const photoBudget = { left: 30 }; // old attachments are fetched for the newest messages only
  try {
    const dialogs = await client.getDialogs({ limit: 25 });
    for (const dialog of dialogs) {
      if (!dialog.entity || isBroadcast(dialog.entity)) continue;
      // One unreadable chat (e.g. a private channel you were removed from → CHANNEL_PRIVATE) must not stop the rest.
      try {
        const messages = await client.getMessages(dialog.entity, { limit: 15 });
        for (const m of [...messages].reverse()) await reportMessage(db, connectionId, m, true, photoBudget);
      } catch (err) {
        console.warn(`Telegram backfill skipped a chat for ${connectionId}: ${(err as Error).message}`);
      }
    }
  } catch (err) {
    console.error(`Telegram backfill failed for ${connectionId}:`, err);
  }
}

async function onConnected(db: Db, connectionId: string, entry: Entry, client: TelegramClient): Promise<void> {
  const me = (await client.getMe()) as Api.User;
  entry.status = "Connected";
  entry.qr = undefined;
  entry.passwordRequired = false;
  entry.error = undefined;
  await saveSession(db, connectionId, client);
  client.addEventHandler((event: NewMessageEvent) => void reportMessage(db, connectionId, event.message, false), new NewMessage({}));
  client.addEventHandler((update: unknown) => void onUpdate(db, connectionId, update), new Raw({}));
  const account = me.username ? `@${me.username}` : [me.firstName, me.lastName].filter(Boolean).join(" ") || me.phone || "Telegram";
  await reportEvent({ connectionId, eventType: "Status", status: "Connected", accountIdentifier: account });
  void backfill(db, connectionId, client);
}

/** Starts a fresh QR sign-in (or resumes a stored session when there is one). */
export async function connect(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  if (entry.status === "Connected" || (entry.status === "Connecting" && entry.client)) return;
  if (!telegramConfigured) {
    entry.status = "Error";
    entry.error = "Telegram is not configured on the server (TELEGRAM_API_ID / TELEGRAM_API_HASH).";
    await reportEvent({ connectionId, eventType: "Status", status: "Error", error: entry.error });
    return;
  }

  entry.status = "Connecting";
  entry.error = undefined;
  entry.passwordRequired = false;
  const stored = await loadSession(db, connectionId);
  const client = newClient(stored ?? "");
  entry.client = client;

  void (async () => {
    try {
      await client.connect();
      if (stored && (await client.checkAuthorization())) {
        await onConnected(db, connectionId, entry, client);
        return;
      }
      await client.signInUserWithQrCode(
        { apiId: config.apiId, apiHash: config.apiHash },
        {
          qrCode: async ({ token, expires }) => {
            entry.qr = `tg://login?token=${token.toString("base64url")}`;
            entry.qrExpiresAt = new Date(expires * 1000);
          },
          password: async () => {
            entry.passwordRequired = true;
            entry.qr = undefined;
            return new Promise<string>((resolve) => { entry.passwordResolver = resolve; });
          },
          onError: async (err) => {
            // A wrong cloud password lets the user try again; anything else ends this attempt.
            if (entry.passwordRequired && /PASSWORD/i.test(err.message)) {
              entry.error = "That password is not correct.";
              return false;
            }
            entry.error = err.message;
            return true;
          },
        },
      );
      await onConnected(db, connectionId, entry, client);
    } catch (err) {
      const message = err instanceof Error ? err.message : "Telegram sign-in failed.";
      const duplicateKey = /AUTH_KEY_DUPLICATED|auth key duplicated/i.test(message);
      entry.status = "Error";
      entry.error = duplicateKey
        ? "Telegram detected a duplicate session. Reconnect and scan the QR code again."
        : message;
      entry.qr = undefined;
      if (duplicateKey) {
        await credentialsCollection(db).deleteMany({ connectionId, credentialKey: SESSION_KEY });
      }
      await reportEvent({ connectionId, eventType: "Status", status: "Error", error: entry.error });
      await client.disconnect().catch(() => undefined);
      entry.client = undefined;
    }
  })();
}

export function submitPassword(connectionId: string, password: string): boolean {
  const entry = sessions.get(connectionId);
  if (!entry?.passwordResolver) return false;
  entry.error = undefined;
  entry.passwordResolver(password);
  entry.passwordResolver = undefined;
  return true;
}

export async function reconnect(db: Db, connectionId: string): Promise<void> {
  await teardown(connectionId, false);
  await connect(db, connectionId);
}

async function teardown(connectionId: string, logOut: boolean): Promise<void> {
  const entry = sessions.get(connectionId);
  if (entry?.client) {
    try {
      if (logOut) await entry.client.invoke(new Api.auth.LogOut());
    } catch { /* already invalid */ }
    await entry.client.disconnect().catch(() => undefined);
    await entry.client.destroy().catch(() => undefined);
  }
  sessions.delete(connectionId);
}

export async function disconnect(db: Db, connectionId: string): Promise<void> {
  await teardown(connectionId, true);
  await credentialsCollection(db).deleteMany({ connectionId, credentialKey: SESSION_KEY });
  await reportEvent({ connectionId, eventType: "Status", status: "Disconnected" });
}

function liveClient(connectionId: string): TelegramClient {
  const entry = sessions.get(connectionId);
  if (!entry?.client || entry.status !== "Connected") throw new Error("Telegram is not connected.");
  return entry.client;
}
const peerOf = (chatId: string) => bigInt(chatId) as never;

export async function sendMessage(connectionId: string, chatId: string, text: string, replyToId?: string): Promise<{ messageId: string; timestamp: string }> {
  const client = liveClient(connectionId);
  const sent = await client.sendMessage(peerOf(chatId), { message: text, ...(replyToId ? { replyTo: Number(replyToId) } : {}) });
  return { messageId: String(sent.id), timestamp: new Date(sent.date * 1000).toISOString() };
}

export type OutgoingFile = { data: Buffer; fileName: string; mimeType: string; caption?: string; replyToId?: string; asDocument?: boolean };

/** Sends a photo, video, voice note, audio or any file, and keeps a copy so the thread can show it straight away. */
export async function sendMedia(db: Db, connectionId: string, chatId: string, f: OutgoingFile): Promise<{ messageId: string; timestamp: string; mediaKind: MediaKind; text: string }> {
  const client = liveClient(connectionId);
  const isImage = f.mimeType.startsWith("image/") && !f.asDocument;
  const limit = isImage ? MAX_PHOTO_BYTES : MAX_FILE_BYTES;
  if (f.data.length === 0) throw new Error("The file is empty.");
  if (f.data.length > limit) throw new Error(`That file is too large to send (limit ${Math.round(limit / 1048576)} MB).`);
  const voice = f.mimeType.startsWith("audio/ogg") && f.asDocument !== true;
  const sent = await client.sendFile(peerOf(chatId), {
    file: new CustomFile(f.fileName, f.data.length, "", f.data),
    caption: f.caption,
    forceDocument: f.asDocument === true || !(isImage || f.mimeType.startsWith("video/") || f.mimeType.startsWith("audio/")),
    voiceNote: voice,
    ...(f.replyToId ? { replyTo: Number(f.replyToId) } : {}),
  });
  const kind: MediaKind = isImage ? "photo" : f.mimeType.startsWith("video/") ? "video" : voice ? "voice" : f.mimeType.startsWith("audio/") ? "audio" : "document";
  const key = { connectionId, chatId, messageId: String(sent.id) };
  await db.collection("telegramMedia").updateOne(key, { $setOnInsert: { ...key, mimeType: f.mimeType, fileName: f.fileName, data: f.data, createdAt: new Date() } }, { upsert: true });
  const label = f.caption || (kind === "photo" ? "📷 Photo" : kind === "voice" ? "🎤 Voice message" : kind === "audio" ? "🎵 Audio" : kind === "video" ? "🎥 Video" : "📄 Document");
  return { messageId: String(sent.id), timestamp: new Date(sent.date * 1000).toISOString(), mediaKind: kind, text: label };
}

/** Marks the chat read (the other person's blue ticks). */
export async function markRead(connectionId: string, chatId: string): Promise<void> {
  await liveClient(connectionId).markAsRead(peerOf(chatId));
}

/** Delete for everyone (revoke) or only for you. */
export async function deleteMessage(connectionId: string, chatId: string, messageId: string, forEveryone: boolean): Promise<void> {
  await liveClient(connectionId).deleteMessages(peerOf(chatId), [Number(messageId)], { revoke: forEveryone });
}

export async function editMessage(connectionId: string, chatId: string, messageId: string, text: string): Promise<void> {
  if (!text.trim()) throw new Error("The message cannot be empty.");
  await liveClient(connectionId).editMessage(peerOf(chatId), { message: Number(messageId), text });
}

export async function forwardMessage(connectionId: string, fromChatId: string, messageId: string, toChatId: string): Promise<{ messageId: string; timestamp: string } | null> {
  const sent = await liveClient(connectionId).forwardMessages(peerOf(toChatId), { messages: [Number(messageId)], fromPeer: peerOf(fromChatId) });
  const first = sent?.[0];
  return first ? { messageId: String(first.id), timestamp: new Date(first.date * 1000).toISOString() } : null;
}

/** Adds an emoji reaction to a message (an empty emoji removes yours). */
export async function react(connectionId: string, chatId: string, messageId: string, emoji: string): Promise<void> {
  const client = liveClient(connectionId);
  await client.invoke(new Api.messages.SendReaction({ peer: await client.getInputEntity(peerOf(chatId)), msgId: Number(messageId), reaction: emoji ? [new Api.ReactionEmoji({ emoticon: emoji })] : [] }));
}

export async function setTyping(connectionId: string, chatId: string, typing: boolean): Promise<void> {
  const client = liveClient(connectionId);
  await client.invoke(new Api.messages.SetTyping({ peer: await client.getInputEntity(peerOf(chatId)), action: typing ? new Api.SendMessageTypingAction() : new Api.SendMessageCancelAction() }));
}

export function presence(connectionId: string, chatId: string) {
  return presenceOf(connectionId, chatId);
}

/** Group or channel details: title, description and members. */
export async function groupInfo(connectionId: string, chatId: string) {
  const client = liveClient(connectionId);
  const entity = await client.getEntity(peerOf(chatId));
  const title = nameOf(entity) ?? "Group";
  let about: string | null = null;
  try {
    if (entity instanceof Api.Channel) about = ((await client.invoke(new Api.channels.GetFullChannel({ channel: entity }))).fullChat as Api.ChannelFull).about ?? null;
    else if (entity instanceof Api.Chat) about = ((await client.invoke(new Api.messages.GetFullChat({ chatId: entity.id }))).fullChat as Api.ChatFull).about ?? null;
  } catch { /* the description is optional */ }
  const people = await client.getParticipants(entity as never, { limit: 200 });
  return {
    subject: title,
    description: about,
    members: people.map((p) => ({
      id: String(p.id),
      name: [p.firstName, p.lastName].filter(Boolean).join(" ") || p.username || String(p.id),
      isAdmin: /Admin|Creator/.test((p as unknown as { participant?: { className?: string } }).participant?.className ?? ""),
    })),
  };
}

/** Fetches older messages of one chat from Telegram and reports each (quietly) so they are stored; returns how many were fetched. */
export async function loadOlder(db: Db, connectionId: string, chatId: string, beforeId?: string, limit = 30): Promise<number> {
  const client = liveClient(connectionId);
  const older = await client.getMessages(peerOf(chatId), { limit: Math.min(limit, 100), ...(beforeId ? { offsetId: Number(beforeId) } : {}) });
  const budget = { left: 10 };
  for (const m of [...older].reverse()) await reportMessage(db, connectionId, m, true, budget);
  return older.length;
}

export { chatKey, STATUS };

export async function restoreSessionsFromDatabase(db: Db): Promise<void> {
  if (!telegramConfigured) {
    console.warn("TELEGRAM_API_ID / TELEGRAM_API_HASH not set — Telegram sign-in is disabled until they are.");
    return;
  }
  const docs = await credentialsCollection(db).find({ credentialKey: SESSION_KEY }).toArray();
  for (const doc of docs) {
    try { await connect(db, doc.connectionId); } catch (err) { console.error(`Could not restore Telegram session ${doc.connectionId}:`, err); }
  }
}

// ── Profile pictures ─────────────────────────────────────────────────────────────────────────────────────────────────
const PIC_TTL_MS = 24 * 3600 * 1000;
const PIC_MISSING_TTL_MS = 6 * 3600 * 1000;
const inflight = new Map<string, Promise<Buffer | null>>();

/** The chat's profile (or group) photo, cached for a day. Returns null when there is none. */
export function profilePicture(db: Db, connectionId: string, chatId: string): Promise<Buffer | null> {
  const key = `${connectionId}:${chatId}`;
  const running = inflight.get(key);
  if (running) return running;
  const job = (async () => {
    const col = db.collection("telegramProfilePics");
    const cached = await col.findOne({ connectionId, chatId });
    if (cached) {
      const age = Date.now() - new Date(cached.fetchedAt as Date).getTime();
      if (cached.missing ? age < PIC_MISSING_TTL_MS : age < PIC_TTL_MS) {
        const raw = cached.data as { buffer?: Buffer } | Buffer | undefined;
        return cached.missing ? null : Buffer.isBuffer(raw) ? raw : (raw?.buffer ?? null);
      }
    }
    const client = sessions.get(connectionId)?.client;
    if (!client) return null;
    try {
      const entity = await client.getInputEntity(bigInt(chatId) as never);
      const data = await client.downloadProfilePhoto(entity as never, { isBig: false });
      if (!Buffer.isBuffer(data) || data.length === 0) throw new Error("none");
      await col.updateOne({ connectionId, chatId }, { $set: { connectionId, chatId, data, fetchedAt: new Date(), missing: false } }, { upsert: true });
      return data;
    } catch {
      await col.updateOne({ connectionId, chatId }, { $set: { connectionId, chatId, missing: true, fetchedAt: new Date() }, $unset: { data: "" } }, { upsert: true });
      return null;
    }
  })().finally(() => inflight.delete(key));
  inflight.set(key, job);
  return job;
}
