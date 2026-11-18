# Limitations

**Unsupported by the Gmail API / Google policy**
- Adding or verifying forwarding addresses and changing auto-forwarding need the restricted `gmail.settings.sharing` scope, which Daksha doesn't request. Existing addresses are listed read-only.
- IMAP/POP are read-only. Inbox type, themes, keyboard shortcuts, undo-send, conversation view and mobile signatures are not exposed by the API.
- Permanent deletion requires the restricted full-access scope and an explicit override + confirmation.
- Forwarding sends the text of the message; attachments are not re-attached.

**Not built in this repository (and why)**
- LLM-based understanding, model selection, multilingual (Hindi) classification and external AI providers — the assistant/model layer is stubbed in the API.
- Central automation engine integration, workflow nodes and the five example workflows; Tasks/finance/travel/calendar modules — stubbed or absent.
- Malware scanning, attachment storage destinations and AI attachment analysis — no infrastructure; attachments are streamed on request only.
- Notification digests/grouping and channels other than in-app records — no delivery infrastructure for Gmail events yet.
- Pub/Sub OIDC JWT verification; per-user API rate limiting.
- Auto-sending follow-ups (deliberately not offered).
- Initial sync processes the 150 most recent matching messages per run; older history is not back-filled beyond that.
- Not verified against a real Google account or at scale.
