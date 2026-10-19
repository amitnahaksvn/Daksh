# Complete work list (nothing missed)

Single checklist of everything still to do, including gaps in the current foundation. `[x]` = done and verified, `[~]` = partial
(what exists is noted), `[ ]` = not started. IDs link to `REQUIREMENTS_TRACKER.md`. Sections are roughly in priority order.

## 0. Blockers needing input from the owner
- [ ] **Master Prompt**: add `docs/Daksh_Master_Prompt.docx` and `.md` unmodified; derive every product requirement into the tracker (REQ-SPEC-001).
- [ ] Decide: AI provider(s) and models, embedding model, identity provider, email/calendar providers (Google/Microsoft), push (FCM/APNs), n8n hosting, cloud/deploy target, data-residency and compliance scope (GDPR/DPDP etc.).
- [ ] Obtain credentials/accounts (never commit): AI API keys, OAuth client IDs, Apple Developer + Google Play accounts, FCM/APNs keys, domain/TLS.

## 1. Repository and process
- [x] Monorepo structure, solution, `global.json` (.NET 10), `.gitignore`, `.env.example`, scripts (REQ-REPO-001).
- [ ] Merge `claude/monorepo-foundation` to `main` via reviewed PR; branch protection, required CI checks, CODEOWNERS.
- [ ] PR template, issue templates, CONTRIBUTING.md, SECURITY.md (vulnerability reporting), LICENSE decision.
- [ ] Dependabot/Renovate for NuGet, npm, pub, Docker, GitHub Actions; pin Actions by SHA.
- [ ] Secret scanning + push protection enabled on GitHub; `scripts/validate.sh` upgraded to a real scanner (gitleaks).
- [ ] Rename `Daksh` vs `daksh` (requested repo name was lowercase `daksh`; current repo is `Daksh`).
- [ ] Versioning/release process and changelog.

## 2. Authentication, authorization, accounts
- [~] JWT bearer wiring + dev auth (REQ-AUTH-001). Remaining:
- [ ] Choose and configure IdP (OIDC): sign-up, sign-in, password reset, MFA, social login.
- [ ] Web: real sign-in (replace placeholder `/login`), token storage strategy (httpOnly cookie / BFF), silent refresh, sign-out, route protection/middleware.
- [ ] Mobile: OIDC (AppAuth/PKCE), secure token storage (Keychain/Keystore), refresh, sign-out.
- [ ] API: user profile table/endpoints, account deletion + full data export (GDPR/DPDP), roles/scopes, authorization policies per endpoint, token audience/issuer validation tests.
- [ ] Remove/lock `Auth:Mode=Development` from all non-local configs; startup check already refuses outside Development/Testing (keep tested).
- [ ] Account management UI (web + mobile): profile, sessions/devices, delete account.

## 3. API and data (`services/api`, `services/shared`)
- [x] Assistant, tasks, reminders, privacy, audit, integrations endpoints; validation; problem details; health; OpenAPI (REQ-API-001/002).
- [ ] Missing endpoints: delete/rename conversation, update/delete task, update/delete reminder, task due-date filters, schedules/recurrence, notifications inbox + mark read, device registration for push, memory CRUD (list/delete memories), integration connect/disconnect/status, data export.
- [ ] Streaming assistant responses (SSE) using `GetStreamingResponseAsync`.
- [ ] Pagination, sorting, filtering conventions; ETags/concurrency for updates.
- [ ] Rate limiting (per user/IP), request size limits, CORS per environment, security headers, HTTPS/HSTS, request logging with correlation ids.
- [ ] API versioning policy; stable OpenAPI published and used to generate TypeScript and Dart clients (replace hand-written clients).
- [ ] Enforce privacy settings: `RetentionDays` purge job (currently stored but **not enforced**), `MemoryEnabled` wiring to memory writes (assistant never writes memories yet).
- [ ] Audit log hardening: append-only (DB permissions/trigger), tamper evidence, retention, PII minimization in `Detail`.
- [ ] Idempotency keys for client POSTs; transactional outbox for jobs/events.
- [ ] Soft delete, timestamps/UTC conventions, indexes review, DB constraints (FKs for tasks/reminders -> users), user table.
- [ ] Migrations: run as a deploy step (not on startup) in non-dev; migration CI check (model/snapshot drift); backup/restore and PITR plan; PostgreSQL version policy.
- [ ] Health: readiness for worker/queue lag; startup probes.

## 4. Worker (`services/worker`)
- [x] Job table, claim with concurrency token, retries with backoff, dead-letter, cancellation, idempotent reminder handler (REQ-WRK-001).
- [ ] Recurring schedules (cron/RRULE) and time zones; reminder snooze/cancel (cancel when reminder deleted).
- [ ] Job types: email sync, calendar sync, memory ingestion/embedding, retention purge, notification fan-out, n8n triggers, ML batch scoring.
- [ ] Failure reporting: dead-letter inspection/requeue endpoint or admin tool, alerts on dead letters and queue lag, metrics (OpenTelemetry).
- [ ] Concurrency tuning, `SKIP LOCKED` claim for high volume, poison-job quarantine, graceful drain on deploy.
- [ ] Worker health endpoint/heartbeat; multi-replica test against real PostgreSQL.

## 5. AI / LLM / ML (`services/ai`) — see `AI_ARCHITECTURE.md`
- [x] Orchestrator, privacy-aware router, guardrails (PII, injection flag), versioned prompts, in-memory memory store, tool registry with confirmation, ML interface, telemetry (REQ-AI-001).
- [ ] Real provider adapters via `IChatClient` (REQ-AI-002); fallback chain, timeouts, retries, circuit breaker, per-user/org budgets and quotas, cost tracking table.
- [ ] Real embeddings + **pgvector** `IMemoryStore` (REQ-AI-003): chunking, metadata, source attribution, per-user isolation tests, delete-on-request, retention.
- [ ] Memory extraction: decide what is remembered, consent UX, review/edit/delete UI.
- [ ] Agent loop: tool calling with function schemas, multi-step planning, confirmation flow end-to-end (API + clients), idempotent tool execution, tool-result prompt-injection defence, MCP client support (REQ-AI-004).
- [ ] Guardrails v2: output moderation, secrets detection, jailbreak classifier, locale-aware PII, allow/deny policies per tool; red-team test set.
- [ ] Conversation context management: token budgeting, summarization of long history, system-prompt files with versions.
- [ ] Speech: recognition + synthesis adapters, streaming audio, push-to-talk UX (see mobile limits).
- [ ] Trained ML models (ONNX/ML.NET): email priority, task urgency, spam, etc.; training data policy, model registry, versioning, offline evaluation, drift monitoring (REQ-AI-005).
- [ ] **Evaluation harness** in CI: golden conversations, retrieval recall@k, guardrail precision/recall, regression gate before prompt/model promotion (REQ-AI-006).
- [ ] Observability: OpenTelemetry traces/metrics export for AI calls; prompt/response logging policy that respects privacy (redacted, opt-in).
- [ ] Responsible-AI docs: limitations, transparency labels (`isSimulated` already surfaced), human-in-the-loop rules, incident process.

## 6. Integrations (`services/integrations`)
- [x] Interfaces and mock adapters for AI, speech, email, calendar, messaging, push, n8n; reported as `Mock` (REQ-INT-001).
- [ ] Email (Gmail/Microsoft Graph): OAuth consent, incremental sync, send with confirmation, least-privilege scopes, token encryption at rest, revoke (REQ-INT-002).
- [ ] Calendar (Google/Microsoft): read, create/update with confirmation, conflict detection.
- [ ] Push: FCM + APNs adapters, device token lifecycle.
- [ ] Messaging: only official APIs (e.g. WhatsApp Business) or user-initiated intents; document limits.
- [ ] n8n: workflow trigger adapter, signed webhooks, allow-list of workflows.
- [ ] Integration state machine: NotConfigured -> ConfiguredUnverified -> Verified via real health probe; per-user connection records; contract tests per adapter with recorded fixtures; sandbox accounts.
- [ ] Secret management for tokens (KMS/Key Vault), rotation, audit of access.

## 7. Web (`apps/web`)
- [x] Landing, login placeholder, dashboard, assistant, tasks, reminders, email/calendar placeholders, integrations, privacy, activity; lint, typecheck, 8 tests, build (REQ-WEB-001).
- [ ] Real auth (section 2), account management, conversation history list + open past conversations (API exists, UI missing), streaming chat, markdown rendering, retry/regenerate.
- [ ] Notifications center, schedules/recurrence UI, task edit/delete/due dates, reminder delete, memory management screen, integration connect flows, data export/delete UI.
- [ ] Email and calendar real screens once adapters exist.
- [ ] Accessibility audit (WCAG 2.2 AA): focus management, skip link, contrast, screen-reader pass, axe in CI; reduced-motion; i18n/RTL plan.
- [ ] Error boundaries (`error.tsx`, `not-found`), loading skeletons, offline state, global toasts.
- [ ] Security: CSP and security headers, CSRF strategy with cookies, no secrets in `NEXT_PUBLIC_*`, remove dev-auth header in production builds.
- [ ] Performance budget, SEO/metadata/OG for landing, analytics only with consent, cookie/consent banner.
- [ ] Playwright E2E (REQ-WEB-007), visual regression optional; component tests for all pages.
- [ ] Generated API client from OpenAPI; shared design tokens; dark mode verified.
- [ ] Next.js is on a very new major (16.x): review upgrade notes in `node_modules/next/dist/docs`, pin and test upgrades.

## 8. Mobile (`apps/mobile`)
- [x] Flutter app: assistant, tasks, integrations, privacy; API client, go_router, Riverpod, logging; analyze clean; 5 tests (REQ-MOB-001).
- [ ] Missing screens/features: dashboard, reminders and schedules, notifications, conversation history, account, login, settings, email/calendar.
- [ ] Auth (section 2), secure storage, push notifications (FCM/APNs), local scheduled notifications, deep links.
- [ ] Permissions UX (microphone, notifications, calendar) with rationale + revoke path; OS manifests/Info.plist strings (REQ-MOB-003 limits documented).
- [ ] Android/iOS release builds: app id/bundle id, signing, flavors (dev/stage/prod), icons/splash, ProGuard, privacy manifests, store listings, TestFlight/Play internal track (REQ-MOB-002).
- [ ] Offline cache/queue, retry policy, connectivity handling, crash reporting (privacy-respecting), accessibility (TalkBack/VoiceOver), localization.
- [ ] Device/integration tests (REQ-MOB-006), golden tests; generated API client.
- [ ] CI builds for Android (and iOS on macOS runner).

## 9. Infrastructure, deployment, operations
- [~] Dockerfiles, compose, k8s template written but **not built/applied** (REQ-INF-001). Remaining:
- [ ] Build and scan images in CI (Trivy), non-root, healthchecks, SBOM; verify `docker compose up` end to end.
- [ ] Choose cloud target; IaC (Terraform/Bicep), environments (dev/stage/prod), managed PostgreSQL with pgvector, secrets manager, networking, TLS, WAF, CDN for web (REQ-INF-004).
- [ ] CD pipelines: build -> migrate -> deploy -> smoke test -> rollback; preview environments for PRs.
- [ ] Monitoring/alerting: logs aggregation, OpenTelemetry collector, dashboards, SLOs, on-call runbook, uptime checks.
- [ ] Backups, restore drills, DR plan; cost controls and budgets (especially AI spend).
- [ ] Load/performance testing; capacity plan for worker and DB.

## 10. Security, privacy, compliance
- [ ] Threat model (STRIDE) incl. prompt injection, data exfiltration through tools, SSRF in integrations.
- [ ] Dependency/vulnerability scanning in CI (dotnet list package --vulnerable, npm audit, pub), SAST (CodeQL).
- [ ] Encryption in transit and at rest, field-level encryption for tokens, key rotation.
- [ ] Privacy: data inventory, lawful basis/consent records, DPA with providers, retention enforced, user export/delete, privacy policy and terms (legal review), children's data stance.
- [ ] Penetration test before launch; abuse/rate-limit review; logging redaction review.
- [ ] Call recording / messaging legality review before any such feature (deferred).

## 11. Testing and CI/CD
- [x] 23 .NET tests, 8 web tests, 5 Flutter tests; CI workflow written (REQ-INF-002: **not yet run on GitHub**).
- [ ] Run CI on GitHub and fix any environment differences; add caching, concurrency cancel, required checks.
- [ ] Coverage reporting and thresholds; mutation testing optional.
- [ ] Cross-component E2E in CI: web/mobile -> API -> PostgreSQL -> worker (Playwright + compose).
- [ ] Contract tests between API and clients (OpenAPI diff check for breaking changes).
- [ ] Postgres test uses `EnsureDeleted` on the named DB: keep restricted to dedicated test DB.
- [ ] Test data builders, flaky-test policy.

## 12. Documentation
- [x] README, ARCHITECTURE, AI_ARCHITECTURE, SETUP, API, MOBILE_PLATFORM_LIMITS, REQUIREMENTS_TRACKER.
- [ ] Keep `API.md` generated/validated against OpenAPI; ADRs for key decisions (.NET 10, Microsoft.Extensions.AI, job table vs broker, pgvector).
- [ ] Runbooks (deploy, rollback, dead-letter handling, incident), onboarding guide, glossary, user-facing help.
- [ ] Update tracker statuses as each item lands; keep numbers (test counts) current.

## 13. Launch readiness
- [ ] Beta plan, feedback loop, support channel, analytics (consented), crash/error budgets.
- [ ] App Store / Play review prep (permissions justification, privacy labels).
- [ ] Go/no-go checklist: all P0 requirements VERIFIED, security review passed, backups tested, on-call ready.
