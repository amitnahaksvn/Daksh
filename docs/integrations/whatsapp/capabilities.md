# WhatsApp capabilities

Daksha's WhatsApp integration connects through **Baileys**, an unofficial client implementation of the
WhatsApp Web protocol — not the official **WhatsApp Business Platform API** (Cloud API / On-Premises API).
This matters because most public documentation and tutorials about "WhatsApp integrations" describe the
Business Platform, which has a completely different capability and restriction set (24-hour messaging
windows, pre-approved message templates, a formal webhook model, official call/contact APIs). None of those
restrictions — and none of those official capabilities — apply here. What follows is specific to this
Baileys-based integration as actually implemented in this codebase.

## What this integration can actually do today

| Capability | Status | Notes |
|---|---|---|
| Send/receive text messages (1:1) | ✅ Real | `services/whatsapp/src/sessionManager.ts` |
| Send/receive text messages (groups) | ✅ Real | Detected via the `@g.us` JID suffix |
| Media messages (image/video/audio/document/sticker/location) | ✅ Detected, not processed | Captured as a type label (e.g. "📷 Photo"); no OCR, transcription, or content understanding |
| Reactions (send/receive) | ✅ Real | `POST /api/integrations/whatsapp/{connectionId}/messages/{messageId}/react` |
| Quoted replies (send/receive) | ✅ Real | Baileys `contextInfo`/`quoted` option |
| AI auto-reply | ✅ Real | Google Gemini via `GeminiChatClient`, gated by Auto Reply settings |
| Live notifications | ✅ Real (with polling fallback) | SignalR hub + REST polling fallback for resilience |
| Message history / chat thread | ✅ Real | Stored in MongoDB `whatsappMessages` |
| QR pairing | ✅ Real | Standard Baileys multi-device linking |

## What this integration cannot do (by design or by platform limitation)

- **No official Business Platform features.** No message templates, no 24-hour customer-service window,
  no official webhook delivery guarantees, no WhatsApp-verified business profile.
- **No reliable contact/business distinction.** Baileys does not expose a trustworthy "is this a WhatsApp
  Business account" flag for arbitrary contacts, so "different behaviour for personal vs business contacts"
  cannot be implemented honestly today.
- **No contact/group directory sync.** There's no local address book — chats are only known once a message
  has been exchanged with them. Anything that requires picking "my saved contacts" or "this specific group"
  from a list (whitelists, per-contact rules, "reply only to contact X") isn't available yet.
- **No reliable call events.** Baileys' call-event support is inconsistent across WhatsApp protocol
  versions; missed-call notifications are not implemented.
- **No content understanding.** Media messages are stored as a type label only — there's no OCR, speech
  transcription, or document parsing. "Detect OTPs/payment requests/meeting invites in message content"
  requires a classification pipeline that doesn't exist yet (see `limitations.md`).
- **Account risk.** Baileys is an unofficial client. WhatsApp can detect and restrict accounts that behave
  like automated clients, and WhatsApp's own protocol changes can break this integration without notice.
  Only connect an account you own and are prepared to risk.

## Where each feature actually lives

- Node/Baileys session: `services/whatsapp/src/sessionManager.ts`
- API endpoints and settings model: `services/api/Daksh.Api/Modules/IntegrationsModule.cs`,
  `services/api/Daksh.Api/Modules/WhatsAppSettingsModule.cs`
- AI auto-reply model: `services/integrations/Daksh.Integrations/GeminiChatClient.cs` (Google Gemini)
- Web UI: `apps/web/src/components/WhatsAppConnectPanel.tsx`, `WhatsAppChatsPanel.tsx`, `WhatsAppSettingsModal.tsx`
