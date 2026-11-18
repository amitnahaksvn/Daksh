# Gmail integration

Daksha connects one or more Gmail accounts per user and offers a **Gmail control centre** inside the **Manage popup** (Apps → Gmail → Manage, or the gear in the mailbox) to
configure how it reads, understands, organises and acts on mail. Manage is the single place for every Gmail setting; there is no separate settings page. The mailbox UI and dashboard card are unchanged except for message actions (archive, trash, star, mark unread).

Flow: **connect → synchronise → understand → categorise → organise → assist → automate → monitor.**

| Area | Where |
|---|---|
| API | `services/api/Daksh.Api/Modules/GmailIntegrationModule.cs` (OAuth, list/read/send, settings) and `Modules/Gmail/*` (everything else) |
| UI | `apps/web/src/components/GmailControlCentre.tsx`, `components/gmail-control/*`, `lib/gmailControl.ts` |
| Tests | `tests/api/Daksh.Api.Tests/GmailManagementTests.cs`, `GmailIntegrationTests.cs`; `GmailControlCentre.test.tsx`, `GmailManagePanel.test.tsx` |

Read next: [implementation-status.md](implementation-status.md) for what is and isn't done, [limitations.md](limitations.md) for what Gmail
or this repository cannot do, [google-cloud-setup.md](google-cloud-setup.md) to make it work against real Google.

Important honesty notes:
- AI understanding is a **local, English-language heuristic classifier**, not an LLM. No email text is sent to any AI provider.
- Daksha's central automation engine and Tasks module are not yet rebuilt on MongoDB in this repository, so Gmail publishes
  events to an outbox collection and records tasks itself. See [automation.md](automation.md).
- Authenticated endpoints and the sync engine have been unit-tested with mocked data but **not exercised against a live Google account**.
