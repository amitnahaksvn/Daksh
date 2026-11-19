import { config } from "./config.js";

export type TelegramEvent = {
  connectionId: string;
  eventType: "Status" | "Message" | "MessageUpdated";
  status?: string;
  accountIdentifier?: string;
  /** The signed-in user's own id: the id of their "Saved Messages" chat. */
  selfChatId?: string;
  error?: string;
  // Message fields
  messageId?: string;
  chatId?: string;
  chatName?: string;
  isGroup?: boolean;
  fromMe?: boolean;
  senderName?: string;
  text?: string;
  timestamp?: string;
  /** Backfilled history: stored, but never notified or auto-replied to. */
  history?: boolean;
  /** The photo's bytes are stored in the telegramMedia collection. */
  hasMedia?: boolean;
  /** photo, video, audio, voice, document or sticker, known even when the bytes were not downloaded. */
  mediaKind?: string;
  mimeType?: string;
  fileName?: string;
  /** The id of the message this one replies to. */
  replyToId?: string;
};

export async function reportEvent(evt: TelegramEvent): Promise<void> {
  try {
    const res = await fetch(`${config.dakshApiBaseUrl}/api/integrations/telegram/internal/events`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Internal-Key": config.internalServiceKey },
      body: JSON.stringify(evt),
    });
    if (!res.ok) console.error(`API rejected ${evt.eventType} event for ${evt.connectionId}: ${res.status}`);
  } catch (err) {
    console.error(`Failed to report ${evt.eventType} for connection ${evt.connectionId}:`, err);
  }
}
