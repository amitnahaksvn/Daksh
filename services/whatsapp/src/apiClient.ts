import { config } from "./config.js";

export type ConnectionEventType =
  | "WhatsAppConnectionStarted"
  | "WhatsAppQrGenerated"
  | "WhatsAppQrExpired"
  | "WhatsAppConnected"
  | "WhatsAppDisconnected"
  | "WhatsAppConnectionFailed"
  | "WhatsAppMessageReceived"
  | "WhatsAppMessageSent"
  | "WhatsAppMessageUpdated";

export async function reportEvent(
  connectionId: string,
  eventType: ConnectionEventType,
  fields: {
    status?: string;
    qr?: string;
    qrExpiresAt?: Date;
    accountIdentifier?: string;
    error?: string;
    messageId?: string;
    chatId?: string;
    chatName?: string;
    preview?: string;
    timestamp?: Date;
  } = {},
): Promise<void> {
  try {
    await fetch(`${config.dakshApiBaseUrl}/api/integrations/whatsapp/internal/events`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Internal-Key": config.internalServiceKey },
      body: JSON.stringify({
        connectionId,
        eventType,
        status: fields.status,
        qr: fields.qr,
        qrExpiresAt: fields.qrExpiresAt?.toISOString(),
        accountIdentifier: fields.accountIdentifier,
        error: fields.error,
        messageId: fields.messageId,
        chatId: fields.chatId,
        chatName: fields.chatName,
        preview: fields.preview,
        timestamp: fields.timestamp?.toISOString(),
      }),
    });
  } catch (err) {
    // The .NET API being briefly unreachable shouldn't take down a WhatsApp session; the next GET
    // /status call will reconcile from this service's own in-memory state regardless.
    console.error(`Failed to report ${eventType} for connection ${connectionId}:`, err);
  }
}
