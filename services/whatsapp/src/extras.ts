/**
 * The parts of WhatsApp that go beyond "text in, text out": delivery/read ticks, deleted and edited messages, every kind of
 * media, profile pictures, the address-book names and the history WhatsApp hands over when a device is linked.
 * Kept apart from sessionManager.ts (which owns sockets and sending) so each piece can be read and tested on its own.
 */
import type { Db } from "mongodb";
import { downloadMediaMessage, normalizeMessageContent, type WAMessage, type WASocket } from "@whiskeysockets/baileys";
import { contactsCollection, messagesCollection } from "./db.js";
import { reportEvent } from "./apiClient.js";

// ── Message status (the ticks) ────────────────────────────────────────────────────────────────────────────────────
/** WhatsApp's own numbering: 0 error, 1 pending (clock), 2 sent (one grey tick), 3 delivered (two grey), 4 read (two blue), 5 played. */
export const STATUS = { error: 0, pending: 1, sent: 2, delivered: 3, read: 4, played: 5 } as const;

/** Moves a message's status forward only — a late "delivered" must never undo "read". */
export async function raiseStatus(db: Db, connectionId: string, messageId: string, status: number): Promise<boolean> {
  const res = await messagesCollection(db).updateOne(
    { connectionId, messageId, fromMe: true, $or: [{ status: { $lt: status } }, { status: { $exists: false } }] },
    { $set: { status } },
  );
  return res.modifiedCount > 0;
}

// ── Deleted and edited messages ───────────────────────────────────────────────────────────────────────────────────
export const DELETED_TEXT = "This message was deleted";

/** The sender removed a message for everyone: keep the tombstone, drop the text and erase any stored photo/file. */
export async function markRevoked(db: Db, connectionId: string, messageId: string): Promise<string | null> {
  const doc = await messagesCollection(db).findOneAndUpdate(
    { connectionId, messageId },
    { $set: { deleted: true, deletedAt: new Date(), text: DELETED_TEXT, hasMedia: false, reactions: [] }, $unset: { quotedText: "" } },
    { returnDocument: "before" },
  );
  await db.collection("whatsappMedia").deleteOne({ connectionId, messageId });
  return doc?.chatId ?? null;
}

/** The sender edited a message: show the new text and remember that it was edited. */
export async function applyEdit(db: Db, connectionId: string, messageId: string, text: string): Promise<string | null> {
  if (!text) return null;
  const doc = await messagesCollection(db).findOneAndUpdate({ connectionId, messageId, deleted: { $ne: true } }, { $set: { text, edited: true } }, { returnDocument: "before" });
  return doc?.chatId ?? null;
}

type UpdateLike = { key: { id?: string | null; remoteJid?: string | null; fromMe?: boolean | null }; update: Record<string, any> };

/** Handles `messages.update`: status ticks, "deleted for everyone" and edits. */
export async function onMessagesUpdate(db: Db, connectionId: string, updates: UpdateLike[]): Promise<void> {
  for (const { key, update } of updates) {
    const id = key.id;
    if (!id) continue;
    let changedChat: string | null = key.remoteJid ?? null;

    // Deleted for everyone: Baileys reports it as a stub of type REVOKE (1), with no message body.
    if (update.messageStubType === 1 || update.message === null) {
      changedChat = (await markRevoked(db, connectionId, id)) ?? changedChat;
      if (changedChat) await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: id, chatId: changedChat });
      continue;
    }

    const edited = update.message?.editedMessage?.message ?? update.message?.protocolMessage?.editedMessage;
    if (edited) {
      const text = textOf(edited);
      changedChat = (await applyEdit(db, connectionId, id, text)) ?? changedChat;
      if (changedChat) await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: id, chatId: changedChat });
      continue;
    }

    if (typeof update.status === "number" && key.fromMe) {
      if (await raiseStatus(db, connectionId, id, update.status) && changedChat) {
        await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: id, chatId: changedChat });
      }
    }
  }
}

/** Group receipts arrive per participant; any delivery counts as delivered and any read as read. */
export async function onReceipts(db: Db, connectionId: string, receipts: { key: { id?: string | null; remoteJid?: string | null }; receipt: { readTimestamp?: unknown; playedTimestamp?: unknown; receiptTimestamp?: unknown } }[]): Promise<void> {
  for (const { key, receipt } of receipts) {
    if (!key.id) continue;
    const status = receipt.playedTimestamp ? STATUS.played : receipt.readTimestamp ? STATUS.read : STATUS.delivered;
    if (await raiseStatus(db, connectionId, key.id, status) && key.remoteJid) {
      await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: key.id, chatId: key.remoteJid });
    }
  }
}

/** A message that arrives as a protocol message (delete / edit) instead of as new content. Returns true when it was one. */
export async function handleProtocolMessage(db: Db, connectionId: string, m: WAMessage): Promise<boolean> {
  const content = normalizeMessageContent(m.message);
  const proto = content?.protocolMessage;
  if (!proto) return false;
  const targetId = proto.key?.id;
  if (targetId && proto.type === 0) {
    const chat = (await markRevoked(db, connectionId, targetId)) ?? m.key.remoteJid;
    if (chat) await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: targetId, chatId: chat });
  } else if (targetId && proto.type === 14 && proto.editedMessage) {
    const chat = (await applyEdit(db, connectionId, targetId, textOf(proto.editedMessage))) ?? m.key.remoteJid;
    if (chat) await reportEvent(connectionId, "WhatsAppMessageUpdated", { messageId: targetId, chatId: chat });
  }
  return true;
}

function textOf(message: any): string {
  const c = normalizeMessageContent(message) ?? message;
  return c?.conversation ?? c?.extendedTextMessage?.text ?? c?.imageMessage?.caption ?? c?.videoMessage?.caption ?? c?.documentMessage?.caption ?? "";
}

// ── Media: photos, videos, voice notes, documents, stickers ───────────────────────────────────────────────────────
export type MediaKind = "image" | "sticker" | "video" | "audio" | "document";
const LIMITS: Record<MediaKind, number> = { image: 8 * 1024 * 1024, sticker: 2 * 1024 * 1024, video: 14 * 1024 * 1024, audio: 14 * 1024 * 1024, document: 14 * 1024 * 1024 };

export function detectMedia(message: WAMessage["message"] | null | undefined): { kind: MediaKind; mimeType?: string; fileName?: string; seconds?: number; voiceNote?: boolean } | null {
  const c = normalizeMessageContent(message);
  if (!c) return null;
  if (c.imageMessage) return { kind: "image", mimeType: c.imageMessage.mimetype ?? "image/jpeg" };
  if (c.stickerMessage) return { kind: "sticker", mimeType: c.stickerMessage.mimetype ?? "image/webp" };
  if (c.videoMessage) return { kind: "video", mimeType: c.videoMessage.mimetype ?? "video/mp4", seconds: c.videoMessage.seconds ?? undefined };
  if (c.audioMessage) return { kind: "audio", mimeType: c.audioMessage.mimetype ?? "audio/ogg", seconds: c.audioMessage.seconds ?? undefined, voiceNote: Boolean(c.audioMessage.ptt) };
  if (c.documentMessage) return { kind: "document", mimeType: c.documentMessage.mimetype ?? "application/octet-stream", fileName: c.documentMessage.fileName ?? undefined };
  return null;
}

/** Downloads the media once and keeps it in "whatsappMedia" so the chat can show or play it. Best-effort. */
export async function storeMedia(db: Db, connectionId: string, m: WAMessage): Promise<void> {
  const info = detectMedia(m.message);
  if (!info || !m.key.id) return;
  try {
    const existing = await db.collection("whatsappMedia").findOne({ connectionId, messageId: m.key.id }, { projection: { _id: 1 } });
    if (!existing) {
      const buffer = (await downloadMediaMessage(m, "buffer", {})) as Buffer;
      if (buffer.length === 0 || buffer.length > LIMITS[info.kind]) return;
      await saveMediaBytes(db, connectionId, m.key.id, buffer, info.mimeType ?? "application/octet-stream");
    }
    await messagesCollection(db).updateOne({ connectionId, messageId: m.key.id }, { $set: { hasMedia: true } });
  } catch (err) {
    console.error(`Failed to download WhatsApp ${info.kind} for connection ${connectionId}:`, err);
  }
}

export async function saveMediaBytes(db: Db, connectionId: string, messageId: string, data: Buffer, mimeType: string): Promise<void> {
  await db.collection("whatsappMedia").updateOne({ connectionId, messageId }, { $setOnInsert: { connectionId, messageId, mimeType, data, createdAt: new Date() } }, { upsert: true });
}

export const mediaLimit = (kind: MediaKind) => LIMITS[kind];

// ── Names ─────────────────────────────────────────────────────────────────────────────────────────────────────────
type ContactLike = { id?: string; lid?: string; phoneNumber?: string; name?: string; notify?: string; verifiedName?: string };

/** Saves address-book names and display names. A person can be known by a phone id or a linked-device (@lid) id, so both are kept
 *  on the same record; partial updates only set the fields they carry, so a name-less update never erases a saved name. */
export async function saveContacts(db: Db, connectionId: string, contacts: ContactLike[]): Promise<void> {
  for (const c of contacts) {
    if (!c.id) continue;
    const isLid = c.id.endsWith("@lid");
    const lid = c.lid ?? (isLid ? c.id : undefined);
    const pn = c.phoneNumber ?? (!isLid && c.id.endsWith("@s.whatsapp.net") ? c.id : undefined);
    const set: Record<string, unknown> = { updatedAt: new Date() };
    if (lid) set.lid = lid;
    if (pn) set.phoneNumber = pn;
    if (c.name) set.name = c.name;
    const notify = c.notify ?? c.verifiedName;
    if (notify) set.notify = notify;
    if (c.verifiedName) set.isBusiness = true;
    try {
      await contactsCollection(db).updateOne({ connectionId, contactId: c.id }, { $set: set }, { upsert: true });
      // A saved name known under the phone id must also be found under the @lid id, which is what chats are keyed by.
      if (c.name && lid && pn) {
        const other = c.id === lid ? pn : lid;
        await contactsCollection(db).updateOne({ connectionId, contactId: other }, { $set: { name: c.name, lid, phoneNumber: pn, updatedAt: new Date() } }, { upsert: true });
      }
    } catch (err) {
      console.error(`Failed to save WhatsApp contact for connection ${connectionId}:`, err);
    }
  }
}

/** Remembers which phone number a linked-device id belongs to, copying any saved name across. */
export async function saveLidMapping(db: Db, connectionId: string, lid: string, pn: string): Promise<void> {
  const contacts = contactsCollection(db);
  const known = await contacts.find({ connectionId, contactId: { $in: [lid, pn] } }).toArray();
  const name = known.find((k) => k.name)?.name;
  const notify = known.find((k) => k.notify)?.notify;
  const set: Record<string, unknown> = { lid, phoneNumber: pn, updatedAt: new Date() };
  if (name) set.name = name;
  if (notify) set.notify = notify;
  for (const id of [lid, pn]) await contacts.updateOne({ connectionId, contactId: id }, { $set: set }, { upsert: true });
}

// ── Profile pictures ──────────────────────────────────────────────────────────────────────────────────────────────
const PIC_TTL_MS = 24 * 3600 * 1000;
const PIC_MISSING_TTL_MS = 6 * 3600 * 1000;
const inflight = new Map<string, Promise<{ data: Buffer; mimeType: string } | null>>();

/** The chat's profile (or group) photo, cached for a day. Returns null when the person has none or hides it. */
export function getProfilePicture(db: Db, sock: WASocket, connectionId: string, chatId: string): Promise<{ data: Buffer; mimeType: string } | null> {
  const key = `${connectionId}:${chatId}`;
  const running = inflight.get(key);
  if (running) return running;
  const job = (async () => {
    const col = db.collection("whatsappProfilePics");
    const cached = await col.findOne({ connectionId, chatId });
    if (cached) {
      const age = Date.now() - new Date(cached.fetchedAt as Date).getTime();
      if (cached.missing ? age < PIC_MISSING_TTL_MS : age < PIC_TTL_MS) {
        return cached.missing ? null : { data: (cached.data as { buffer: Buffer }).buffer ?? (cached.data as Buffer), mimeType: (cached.mimeType as string) ?? "image/jpeg" };
      }
    }
    try {
      const url = await sock.profilePictureUrl(chatId, "image", 8000);
      if (!url) throw new Error("none");
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = Buffer.from(await res.arrayBuffer());
      const mimeType = res.headers.get("content-type") ?? "image/jpeg";
      await col.updateOne({ connectionId, chatId }, { $set: { connectionId, chatId, data, mimeType, fetchedAt: new Date(), missing: false } }, { upsert: true });
      return { data, mimeType };
    } catch {
      // No photo, or the person's privacy settings hide it from us: remember that for a few hours instead of asking again.
      await col.updateOne({ connectionId, chatId }, { $set: { connectionId, chatId, missing: true, fetchedAt: new Date() }, $unset: { data: "" } }, { upsert: true });
      return null;
    }
  })().finally(() => inflight.delete(key));
  inflight.set(key, job);
  return job;
}

/** The picture changed or was removed: forget the cached copy so the next request fetches the new one. */
export async function forgetProfilePicture(db: Db, connectionId: string, chatId: string): Promise<void> {
  await db.collection("whatsappProfilePics").deleteOne({ connectionId, chatId });
}
