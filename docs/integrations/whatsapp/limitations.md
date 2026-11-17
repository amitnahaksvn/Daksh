# WhatsApp integration — known limitations

Honest list of what the Settings Center shows but doesn't (yet) fully enforce, and why. None of these are
hidden behind a switch that looks live but silently does nothing — each one is either disabled with an
explanation in the UI, or documented here as a partial implementation.

## What is not available
- **Mobile push notifications** — there is no push infrastructure (no mobile app registration, no FCM),
  so the option isn't offered. Dashboard, desktop (browser), email and WhatsApp-to-yourself are.
- **Daksha's main Tasks / Reminders modules** — their API endpoints are currently stubs returning 501, so
  "create a reminder" and "add to tasks" use a WhatsApp-specific list under Message Intelligence instead.
- **A phone address book** — Daksha only knows the contacts WhatsApp reports and the chats that have exchanged
  messages, so contact pickers list those rather than your whole phone book.

## Implemented with caveats
- **Business messages** — based on WhatsApp reporting a business profile for the contact (looked up once
  per contact, plus verified names from contact sync). A business account WhatsApp won't return a profile
  for is treated as personal.
- **Saved contacts vs unknown numbers** — "saved" means WhatsApp reported an address-book name for that
  contact (`contacts.upsert`/`contacts.update` → `whatsappContacts`). If it never sends a name, that person
  is treated as unknown. 1:1 chats only.
- **Missed calls** — recorded from Baileys' `call` event when a call times out unanswered, and only for calls
  that arrive while Daksh's session is connected. 1:1 calls only; they appear as "📞 Missed voice call" /
  "📹 Missed video call" and never trigger an auto-reply.
- **Message Intelligence** — pattern (keyword/regex) matching on message text, not AI: fast and free, but it
  can miss things or over-match. OTP codes are tagged for notification rules but never stored.
- **Desktop notifications** — browser notifications while a Daksha tab is open; they need the browser's
  permission and don't work with the tab closed.
- **WhatsApp-to-yourself notifications** — sent to your own number's "Message yourself" chat, so they show
  up as messages in that chat in Daksha too.
- **Email notifications** — sent through the configured Gmail SMTP account to your account's address.
- **Selected / blocked / AI-excluded chats, per-chat rules** — chosen from chats that have already exchanged
  messages with this account.
- **Out-of-hours message** — uses the active-hours window and timezone from General; when set, it replaces
  the AI/custom reply for messages arriving outside that window.
- **Reply in the sender's language** — an instruction in the prompt, so quality depends on the model.
- **Summarize long conversations** — one extra AI request per reply, summarising up to the 40 messages older
  than the context window; skipped if that request fails.

## Stored but only partially enforced
- **Message retention (days)** (Privacy) — the number is saved, but no background job walks stored
  messages and deletes ones past that age. "Delete stored messages now" in the same tab is the real,
  immediate action; wiring automatic enforcement would mean a recurring job in `Daksh.Worker`.
- **Require approval before sending** (Auto Reply) — implemented as "hold for approval, then send exactly
  as drafted." There's no way to edit a pending reply's text before approving it.

## Platform-level, not fixable by this codebase
- Baileys is an unofficial WhatsApp Web client. WhatsApp can change its protocol at any time and break
  pairing or messaging without notice, and can restrict accounts it detects behaving like automated
  clients.
- Delivery to some JID types (e.g. the newer `@lid` linked-device addressing) can occasionally behave
  differently from the classic `@s.whatsapp.net` phone-number JIDs; this integration sends to whatever JID
  Baileys reports for a chat and can't control WhatsApp server-side delivery behaviour.
- AI auto-reply depends on Google's Gemini API being reachable and within quota. `GeminiChatClient` retries
  once on HTTP 429 (rate limited) and 503 (overloaded), but a sustained outage or quota exhaustion means
  auto-reply silently does nothing for that message (logged as a warning, never surfaced as a user-facing
  error, since this all runs off the back of a webhook with no one waiting on the response).
