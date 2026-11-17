import { config } from "./config.js";

export type TelegramEvent = {
  connectionId: string;
  eventType: "Status" | "Message";
  status?: string;
  accountIdentifier?: string;
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
