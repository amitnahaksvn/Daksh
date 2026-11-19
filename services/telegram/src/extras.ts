/**
 * Everything live that happens to Telegram messages after they were stored: read ticks, deletes, edits, reactions,
 * typing and online status. Telegram pushes these as raw updates; each one is turned into a change on the stored
 * message (or a short-lived in-memory state for typing) and reported so open threads refresh.
 */
import type { Db } from "mongodb";
import { Api, utils } from "telegram";
import { reportEvent } from "./apiClient.js";

/** 1 pending, 2 sent, 4 read: the same numbers the WhatsApp ticks use, so the web shows both the same way. */
export const STATUS = { pending: 1, sent: 2, read: 4 } as const;

const messages = (db: Db) => db.collection("telegramMessages");
const changed = (connectionId: string, chatId: string, messageId?: string) => reportEvent({ connectionId, eventType: "MessageUpdated", chatId, messageId });

// ── Typing and online status (kept in memory; they are only true for a few seconds) ──────────────────────────────────
type Live = { state: "typing" | "recording" | "uploading" | "online" | "offline" | "recently"; lastSeen?: number; at: number };
const live = new Map<string, Live>();
export const liveKey = (connectionId: string, chatId: string) => `${connectionId}:${chatId}`;

export function presenceOf(connectionId: string, chatId: string): { state: string; lastSeen?: number } {
  const known = live.get(liveKey(connectionId, chatId));
  if (!known) return { state: "unknown" };
  if ((known.state === "typing" || known.state === "recording" || known.state === "uploading") && Date.now() - known.at > 8000) return { state: "online" };
  return { state: known.state, lastSeen: known.lastSeen };
}

function actionState(action: unknown): Live["state"] | null {
  if (action instanceof Api.SendMessageTypingAction) return "typing";
  if (action instanceof Api.SendMessageRecordAudioAction || action instanceof Api.SendMessageRecordVideoAction) return "recording";
  if (action instanceof Api.SendMessageCancelAction) return null;
  return "uploading";
}

/** A peer's id as the string the rest of the app uses for a chat. */
export const chatKey = (peer: Parameters<typeof utils.getPeerId>[0]) => String(utils.getPeerId(peer));

/** Handles one raw Telegram update. */
export async function onUpdate(db: Db, connectionId: string, update: unknown): Promise<void> {
  try {
    // Outgoing messages the other side has read (the blue ticks).
    if (update instanceof Api.UpdateReadHistoryOutbox || update instanceof Api.UpdateReadChannelOutbox) {
      const chatId = update instanceof Api.UpdateReadChannelOutbox ? `-100${update.channelId}` : chatKey(update.peer);
      const res = await messages(db).updateMany(
        { ConnectionId: connectionId, ChatId: chatId, FromMe: true, $expr: { $lte: [{ $toLong: "$MessageId" }, update.maxId] }, $or: [{ Status: { $lt: STATUS.read } }, { Status: { $exists: false } }] },
        { $set: { Status: STATUS.read } },
      );
      if (res.modifiedCount > 0) await changed(connectionId, chatId);
      return;
    }
    // Deleted for everyone by the other person.
    if (update instanceof Api.UpdateDeleteMessages || update instanceof Api.UpdateDeleteChannelMessages) {
      const ids = update.messages.map(String);
      const filter: Record<string, unknown> = { ConnectionId: connectionId, MessageId: { $in: ids } };
      if (update instanceof Api.UpdateDeleteChannelMessages) filter.ChatId = `-100${update.channelId}`;
      const docs = await messages(db).find(filter).project({ ChatId: 1, MessageId: 1 }).toArray();
      if (docs.length === 0) return;
      await messages(db).updateMany(filter, { $set: { Deleted: true, Text: "This message was deleted", HasMedia: false, Reactions: [] } });
      await db.collection("telegramMedia").deleteMany({ connectionId, messageId: { $in: ids } });
      for (const chatId of new Set(docs.map((d) => String(d.ChatId)))) await changed(connectionId, chatId);
      return;
    }
    // Edited.
    if (update instanceof Api.UpdateEditMessage || update instanceof Api.UpdateEditChannelMessage) {
      const m = update.message;
      if (!(m instanceof Api.Message) || !m.peerId || !m.message) return;
      const chatId = chatKey(m.peerId);
      const res = await messages(db).updateOne({ ConnectionId: connectionId, ChatId: chatId, MessageId: String(m.id), Deleted: { $ne: true } }, { $set: { Text: m.message, Edited: true } });
      if (res.modifiedCount > 0) await changed(connectionId, chatId, String(m.id));
      return;
    }
    // Reactions.
    if (update instanceof Api.UpdateMessageReactions) {
      const chatId = chatKey(update.peer);
      const reactions = (update.reactions?.results ?? []).flatMap((r) => (r.reaction instanceof Api.ReactionEmoji ? [{ Emoji: r.reaction.emoticon, Count: r.count, Mine: r.chosenOrder !== undefined }] : []));
      await messages(db).updateOne({ ConnectionId: connectionId, ChatId: chatId, MessageId: String(update.msgId) }, { $set: { Reactions: reactions } });
      await changed(connectionId, chatId, String(update.msgId));
      return;
    }
    // Typing.
    if (update instanceof Api.UpdateUserTyping || update instanceof Api.UpdateChatUserTyping || update instanceof Api.UpdateChannelUserTyping) {
      const chatId = update instanceof Api.UpdateUserTyping ? String(update.userId) : update instanceof Api.UpdateChatUserTyping ? `-${update.chatId}` : `-100${update.channelId}`;
      const state = actionState(update.action);
      if (state) live.set(liveKey(connectionId, chatId), { state, at: Date.now() });
      else live.set(liveKey(connectionId, chatId), { state: "online", at: Date.now() });
      return;
    }
    // Online / last seen.
    if (update instanceof Api.UpdateUserStatus) {
      const s = update.status;
      const entry: Live = s instanceof Api.UserStatusOnline ? { state: "online", at: Date.now() }
        : s instanceof Api.UserStatusOffline ? { state: "offline", lastSeen: s.wasOnline, at: Date.now() }
        : { state: "recently", at: Date.now() };
      live.set(liveKey(connectionId, String(update.userId)), entry);
    }
  } catch (err) {
    console.warn(`Telegram update handling failed for ${connectionId}: ${(err as Error).message}`);
  }
}
