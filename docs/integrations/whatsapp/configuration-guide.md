# WhatsApp configuration guide

The Settings Center (shown inline on Apps → WhatsApp → Manage, or in the wide settings popup) controls
everything below. Settings are per-connection, stored in MongoDB, and persist whether the WhatsApp socket
is currently connected or not.

## Where settings live

- **Basic flags** (`NotificationsEnabled`, `AutoReplyEnabled`, `AutoReplyInGroups`) — on the
  `IntegrationConnection` document (`integrationConnections` collection). These predate the Settings Center
  and are still the source of truth for the modal's General/Auto Reply toggles of the same name.
- **Everything else** — one `WhatsAppSettings` document per connection in the `whatsappSettings` collection.
  Created lazily (with defaults) the first time settings are read or written for a connection.

## Tabs

### General
- **Pause all automation** — a hard stop: while on, nothing auto-replies and nothing reacts on Daksha's
  behalf, regardless of any other setting. The connection itself stays live.
- **Incoming message notifications** — gates the bell/toast for new messages. Enforced in
  `IntegrationsModule.ReceiveWhatsAppEventAsync`.
- **Outgoing message notifications** — when on, messages you send (including from your phone) also appear in the bell.
- **Operate continuously / Active hours / Timezone** — when "operate continuously" is off, auto-reply only
  fires within the configured local time window (timezone as an IANA id, e.g. `Asia/Kolkata`). An overnight
  window (start > end) is handled as wrapping past midnight.

### Monitoring
Each toggle gates whether a message of that kind is (a) reported as a live notification and (b) eligible
for auto-reply. It never affects storage — every message is always kept in the chat thread regardless of
these toggles, since the thread view needs to show your actual conversation. Media-type toggles are
detected from the same emoji-prefixed label the chat thread displays (📷/🎥/🎤/📄/📍); link detection is a
simple `http(s)://` substring match.

### Auto Reply
- **Scope**: "All contacts" or "Unknown contacts only" — "unknown" means no message has ever been sent
  *from* this account to that chat before (a simple heuristic); the Contacts & Groups tab adds saved/unknown/business awareness.
- **Custom message**: when enabled, auto-reply sends this fixed text instead of calling the AI.
- **Response delay / Cooldown**: delay before sending; cooldown prevents replying again to the same chat
  within N seconds, tracked in `whatsappAutoReplyState`.
- **Require approval**: instead of sending automatically, the generated reply is stored in
  `whatsappPendingReplies` and shown in the Auto Reply tab for you to Send or Discard.

### AI Behaviour
Personality, response length, custom instructions, and "never respond to these topics" are all folded
directly into the prompt sent to Gemini (`IntegrationsModule.BuildAutoReplyPrompt`). "Never respond" works
by instructing the model to output the literal token `NO_REPLY` for a matching topic, which the backend
checks for and silently drops instead of sending.

Conversation context (if enabled) pulls the last N stored messages for that chat as history for the AI
call — gated additionally by Privacy's "Allow message content as AI context", which overrides this off
regardless of what's configured here.

### Privacy & Security
- **Hide message previews** strips the preview text from the live notification payload server-side (not
  just hidden in the UI) — the SignalR event itself carries `preview: null` when this is on.
- **Delete stored messages now** is an immediate, irreversible purge of this connection's
  `whatsappMessages` — the real action behind "configure message retention," alongside the retention-days setting (see `limitations.md`).

## Partial updates
The modal always loads the full settings document, but `PATCH /api/integrations/whatsapp/{connectionId}/full-settings`
only applies the sections/fields actually present in the request body — saving the Auto Reply tab never
touches AI Behaviour's saved values, etc. The modal's Save button computes this diff automatically.

## Contacts & Groups
Per-chat rules (monitor on/off, auto-reply Default/Always/Never, priority, exclude from AI, custom reply)
live in `whatsappChatRules` and save immediately. They override the connection-wide settings, and combine
with the pickers on the Auto Reply and Privacy tabs.

## Notifications
Choose between every incoming message and only selected types (questions, deadlines, payments, meetings,
OTPs, suspicious links, attachments, unknown numbers, priority contacts). High-priority chats always notify.
Delivery: dashboard bell/toast, desktop (browser), email, and a WhatsApp message to yourself, plus an
optional sound.

## Message Intelligence
Switch detection categories on or off, and browse what Daksha has picked up — detected items, tasks,
reminders, summaries and saved messages — in `whatsappInsights`.

## Automation Rules
See `automation-rules.md`.

