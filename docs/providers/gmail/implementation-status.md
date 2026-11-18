# Implementation status

Legend: ✅ implemented and unit-tested · 🟡 partial · ⚙️ needs external configuration · ❌ not implemented / unsupported

| Feature | Status | Notes |
|---|---|---|
| Multiple accounts, nickname, default, enable/disable, disconnect, reconnect, test, status set | ✅ | Workspace detected from domain |
| OAuth (PKCE, state, encrypted tokens, refresh, revoke, incremental scopes) | ✅ ⚙️ | Needs Google credentials; verification for public use |
| Backend permission enforcement (profiles + overrides + scope check) | ✅ | |
| Audit log with filters | ✅ | |
| Read, search (builder + saved), threads, drafts, reply/forward (threaded), labels, archive/trash/restore/star/read | ✅ ⚙️ | Not run against live Gmail |
| Filters, vacation, language, send-as signature | ✅ ⚙️ | Needs `settings` scope |
| Forwarding addresses / auto-forwarding / IMAP / POP | 🟡 | Read-only |
| Permanent delete | ✅ ⚙️ | Needs restricted scope + override + confirm |
| Sync: initial, incremental, resync, dedupe, retry/backoff, dead letters | ✅ | Unit-tested pieces; engine not run live |
| Pub/Sub push + watch renewal + reconciliation | ✅ ⚙️ | Needs topic/subscription |
| AI classification, summaries, action items, deadlines, injection guard, corrections | 🟡 | Heuristic, English only; no LLM |
| AI model/language selection, external providers | ❌ | No AI layer to connect |
| Rules engine, test/preview/run/history, confirmation for destructive | ✅ | |
| Auto-reply policy, drafts, gated auto-send, limits, one per thread | ✅ | Template text, not model-written |
| Follow-ups (track, due, snooze, draft, complete, ignore, notify once) | ✅ | Never auto-sent |
| Notifications (16 types, dedupe, quiet hours, cap) | 🟡 | In-app records only; no digest/channels |
| Attachments (list, safe download, type check, classify, duplicate, retention) | 🟡 | No storage, scanner or AI analysis |
| Automation: events outbox, rule actions | 🟡 | Engine/workflows/tasks modules stubbed |
| Example workflows 1–5 | ❌ | Depend on stubbed modules |
| Export and deletion of local data; retention purge | ✅ | |
| Manage popup UI (simple tabs + 10 control-centre tabs, unsaved-changes guard, version conflicts) | ✅ | |
| Docs | ✅ | This folder |

Verification at time of writing: backend build ✅; 108 Gmail tests ✅; frontend type-check/lint (new files) ✅, 25 tests ✅, production build ✅. Eight pre-existing API tests need a local MongoDB and fail without one.
