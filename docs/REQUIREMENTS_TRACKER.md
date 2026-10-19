# Requirements tracker

Statuses: NOT_STARTED, IN_PROGRESS, BLOCKED, IMPLEMENTED_UNVERIFIED, VERIFIED, DEFERRED_WITH_REASON, NOT_APPLICABLE_WITH_REASON.

**BLOCKER (REQ-SPEC-001):** `Daksh_Master_Prompt.docx` / `.md` were not available in the workspace or repository, so they were not
copied into `docs/`, and no product requirements have been derived from them. The IDs below cover the *repository foundation*
brief only. Once the master prompt is supplied, import its requirements here.

| ID | Requirement | Priority | Owner | Status | Evidence | Blockers |
|----|-------------|----------|-------|--------|----------|----------|
| REQ-SPEC-001 | Master Prompt (docx + md) stored in `docs/` unmodified | P0 | Product owner | BLOCKED | Files not found | Provide the two files |
| REQ-REPO-001 | Monorepo structure, solution, root config | P0 | Eng | VERIFIED | `dotnet build Daksh.sln` OK | |
| REQ-API-001 | ASP.NET Core API on .NET 10 with validation, error handling, health, OpenAPI, logging | P0 | Eng | VERIFIED | 9 API tests + live run on PostgreSQL | |
| REQ-API-002 | PostgreSQL + EF Core migrations | P0 | Eng | VERIFIED | Migration applied to PostgreSQL 16 (integration test) | Not yet run against pgvector image |
| REQ-AUTH-001 | OIDC authentication integration | P0 | Eng | IMPLEMENTED_UNVERIFIED | JWT bearer wiring; Dev mode tested | No identity provider configured |
| REQ-WRK-001 | Worker: jobs, retries, backoff, dead-letter, cancellation, idempotency | P0 | Eng | VERIFIED | 6 worker tests; live API->worker run on PostgreSQL | |
| REQ-E2E-001 | Request -> API validates -> response -> audit event | P0 | Eng | VERIFIED | API tests; curl against running API+PostgreSQL | |
| REQ-AI-001 | Provider-agnostic AI core (IChatClient, routing, guardrails, prompts) | P0 | Eng | VERIFIED | 7 Daksh.Ai tests | Only mock model exists |
| REQ-AI-002 | Real LLM provider adapter(s) | P0 | Eng | NOT_STARTED | | API key + provider choice |
| REQ-AI-003 | Persistent RAG memory (pgvector) with real embeddings | P1 | Eng | NOT_STARTED | In-memory store only | Embedding provider |
| REQ-AI-004 | Agent tool calling with confirmation (email/calendar actions) | P1 | Eng | IN_PROGRESS | Registry + confirmation rule tested | Real tools need integrations |
| REQ-AI-005 | Trained ML models (ONNX/ML.NET), e.g. email priority | P2 | Eng | NOT_STARTED | Heuristic placeholder only | Training data |
| REQ-AI-006 | AI evaluation harness gated in CI | P1 | Eng | NOT_STARTED | | |
| REQ-INT-001 | Integration interfaces + mock adapters (AI, speech, email, calendar, messaging, push, n8n) | P0 | Eng | VERIFIED | Tests; `/integrations` reports Mock | |
| REQ-INT-002 | Real email/calendar/push/n8n/speech adapters | P1 | Eng | NOT_STARTED | | Credentials, OAuth apps |
| REQ-WEB-001 | Next.js site: landing, assistant, tasks, reminders, email/calendar, integrations, privacy, activity | P0 | Eng | VERIFIED | lint, typecheck, 8 tests, `next build` pass | Sign-in is a placeholder (AUTH-001) |
| REQ-WEB-007 | Browser E2E tests (Playwright) | P2 | Eng | NOT_STARTED | | |
| REQ-MOB-001 | Flutter app: assistant, tasks, integrations, privacy, API client, nav, logging | P0 | Eng | VERIFIED | `flutter analyze` clean, 5 tests | Android/iOS builds not executed |
| REQ-MOB-002 | Android/iOS release builds | P1 | Eng | NOT_STARTED | | Android SDK; macOS/Xcode for iOS |
| REQ-MOB-006 | Device integration tests; notifications; reminders/schedules UI | P2 | Eng | NOT_STARTED | | |
| REQ-MOB-003 | Background mic, cellular call control, third-party messaging | P2 | Product | DEFERRED_WITH_REASON | `docs/MOBILE_PLATFORM_LIMITS.md` | OS restrictions |
| REQ-INF-001 | Dockerfiles, compose, env example, gitignore | P0 | Eng | IMPLEMENTED_UNVERIFIED | Files written | Docker daemon not available to build images in the authoring session |
| REQ-INF-002 | GitHub Actions CI | P0 | Eng | IMPLEMENTED_UNVERIFIED | `.github/workflows/ci.yml` | Not yet run on GitHub |
| REQ-INF-004 | Deployment target and templates | P2 | Eng | NOT_STARTED | Unverified k8s template | Target decision |
| REQ-GOV-001 | Repo governance: PR flow, branch protection, CODEOWNERS, templates, dependency updates, secret scanning | P1 | Eng | NOT_STARTED | | Merge decision |
| REQ-DATA-001 | Enforce retention, data export and account deletion; audit log hardening | P0 | Eng | NOT_STARTED | `RetentionDays` stored but not enforced | |
| REQ-API-003 | Complete API surface (CRUD, schedules, notifications, devices, memory, streaming, rate limits) | P0 | Eng | NOT_STARTED | | |
| REQ-OPS-001 | Observability, alerting, backups, runbooks | P1 | Eng | NOT_STARTED | | Cloud target |
| REQ-SEC-001 | Threat model, scanning (CodeQL, deps, images), pen test | P0 | Eng | NOT_STARTED | | |
| REQ-PRIV-001 | Privacy policy/terms, consent records, DPAs, compliance review | P0 | Product | NOT_STARTED | | Legal input |
| REQ-A11Y-001 | WCAG 2.2 AA audit web + mobile | P1 | Eng | NOT_STARTED | | |
| REQ-REL-001 | Beta, store submissions, go/no-go checklist | P1 | Product | NOT_STARTED | | Store accounts |

## Next actions

The exhaustive checklist of all remaining work is in [ROADMAP.md](ROADMAP.md).

1. **Provide the Master Prompt** (`Daksh_Master_Prompt.docx` and `.md`) in `docs/` and import its requirements into this tracker (REQ-SPEC-001).
2. **Real LLM + embeddings, and persistent memory**: add an `IChatClient` adapter for the chosen provider and a PostgreSQL + pgvector `IMemoryStore` (REQ-AI-002, REQ-AI-003).
3. **Identity**: configure an OIDC provider for the API, web and mobile sign-in (REQ-AUTH-001).
4. **Real integrations and confirmed-action tools**: email, calendar and push adapters, then agent tools that require user confirmation (REQ-INT-002, REQ-AI-004).
5. **Quality gates**: AI evaluation harness and web Playwright tests in CI, and confirm the first GitHub Actions run (REQ-AI-006, REQ-WEB-007, REQ-INF-002).
