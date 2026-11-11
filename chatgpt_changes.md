# Implementation status — Daksh agent platform

Last updated: 2026-11-11, paused by request mid-session. This file is the resume point: read it, then
`docs/IMPLEMENTATION_ROADMAP.md` and `docs/FEATURE_PARITY_MATRIX.md`, before continuing.

## Where things stand

Phase 0 (audit) is complete. Phases 1–4 (foundation, identity, model providers, Gateway + agent runtime) are built
and the whole solution compiles: `dotnet build Daksh.sln` → 0 errors, 0 warnings, including the untouched legacy
`Daksh.Api`/`Daksh.Worker`/`Daksh.Ai` projects. Phases 5–15 (sandbox hardening, memory polish, multi-agent,
channels beyond the mock, skills, plugins beyond the example, voice/media, devices, Control UI, CLI completeness,
hardening, final verification) are **not started** beyond what's noted below.

**Nothing has been committed.** Everything in this message is staged (`git add`) but not committed, so the owner
can review the diff before it becomes a commit. The pre-existing uncommitted changes in `apps/web` and
`apps/mobile` (present before this session started) were left exactly as they were — not staged, not touched.

## What is built and compiles

- **Domain model** (`services/platform/Daksh.Platform.Core/Domain`): ~55 entities covering identity, workspaces,
  roles/permissions, agents, providers/models, sessions/messages/executions, tools/approvals, memory, skills,
  plugins, channels, automation, devices, files, usage/cost, audit.
- **Security primitives** (`.../Security/Crypto.cs`, `Guards.cs`): token/password hashing, AES-GCM secret
  envelope with key rotation, TOTP, a secret redactor, a workspace-jailed path resolver that follows symlinks
  before deciding, an SSRF guard that resolves DNS itself and checks the address actually connected to, a
  deny-pattern command policy, and the untrusted-content wrapper used everywhere outside content reaches a model.
- **Tool framework** (`.../Tools/Contracts.cs`): `ITool`, JSON-schema validation, tool groups/profiles, and the
  full allow/alsoAllow/deny/approve policy evaluator (deny always wins).
- **Model providers** (`.../Providers/*`): OpenAI, Anthropic, Ollama adapters against the real wire formats
  (streaming, tool calls, vision, embeddings), and a **scripted provider** with two behaviours — `echo` and a
  rule-based `research` behaviour that drives the required end-to-end scenario deterministically and offline.
  `ModelCallRunner` (in `Daksh.Platform`) resolves "provider/model" refs, rotates credentials on auth/rate-limit
  failures with growing cooldowns, and records usage/cost.
- **Agent runtime** (`Daksh.Platform/Runtime/AgentRuntimeEngine.cs`): the full step loop — prompt building with
  instructions + memory recall + history, streaming, tool-call execution with policy/approval/risk checks, loop
  detection, context compaction via a summarizing model call, usage/cost recording. `RuntimeWorker` claims queued
  or lease-expired executions (SKIP-LOCKED-style, lease renewal in the background) so a restart resumes work
  instead of losing it.
- **Memory** (`Daksh.Platform/Memory/MemoryService.cs`): pgvector cosine search fused (reciprocal-rank) with
  PostgreSQL full-text search; falls back to full-text alone with no embedding model configured; dedupe by
  normalized-content hash.
- **Automation** (`Daksh.Platform/Automation/*`): `ScheduleCalculator` (at/every/cron, IANA time zones, retry
  backoff) and `JobRunnerWorker`, which claims due jobs with `FOR UPDATE SKIP LOCKED` and runs the agent prompt
  action through the same runtime engine.
- **Channels**: `IChannelAdapter` contract, DM/group access policy evaluated in code, and a `MockChannelAdapter` +
  `ChannelService` + `ChannelOutboxWorker` sufficient for the required scenario's "deliver through a channel" step
  and for automated channel tests. No real channel (Telegram/Discord/Slack/WhatsApp) is wired yet.
- **Sandbox**: a container backend (Docker, no network, resource limits — the real boundary) and a process
  backend (explicitly documented as not a boundary), selected per agent and capped by an instance-wide ceiling.
- **Identity/workspaces**: register/login/refresh-with-reuse-detection/MFA scaffolding, six built-in roles with a
  real permission set, tenant isolation enforced in `PlatformDbContext` (query filters + a save-time guard that
  rejects a write into another workspace even if a check upstream was missed).
- **Gateway** (`services/gateway/Daksh.Gateway`): ASP.NET Core host, JWT auth, SignalR hub for live run events,
  REST modules for auth/workspaces/providers/agents/sessions/approvals/automations/usage/mock-channel testing,
  OpenTelemetry, Serilog, health checks, OpenAPI.
- **CLI, reference desktop node, example plugin**: minimal but real — the CLI talks to the Gateway's REST API,
  the node demonstrates the pairing/SignalR protocol (pairing endpoints on the Gateway side are not built yet,
  so it cannot complete a real pairing today), the plugin runs the full stdio JSON-RPC lifecycle.
- **Database**: one EF Core migration (`InitialCreate`) applied cleanly to real PostgreSQL 17 with pgvector
  (58 tables; verified with `\dx` and `\d` against the running container). `pgvector` extension creation and the
  unmapped `vector` column are added as raw SQL inside the migration (see TD-012).

## Verified by actually running it (not just "it compiles")

- Migration applied to a live `pgvector/pgvector:pg17` container; `vector` extension and column confirmed present.
- Gateway started against that database, `/health/ready` → Healthy, background workers (runtime, scheduler,
  channel outbox) confirmed polling in the logs.
- `POST /api/v1/auth/register` → issues a workspace with all six built-in roles and the owner's full permission
  set, confirmed against the database (`WorkspaceMembers`, `RolePermissions`, `Permissions` rows all correct).
- Found and fixed three real bugs this way, not caught by `dotnet build`:
  1. `JobRunnerWorker` took a scoped `SchedulerService` directly in a singleton's constructor → DI validation
     failure at startup. Fixed: resolve it per-scope inside the job loop instead.
  2. `GET /testing/mock-channel/accounts/{id}/outbound` took the unregistered-as-a-known-service
     `MockChannelAdapter` as a plain parameter → minimal APIs tried to infer it as a request body on a GET and
     refused to start. Fixed: `[FromServices]`.
  3. **Not yet fixed** — see "Known bug, fix identified" below.

## Known bug, fix identified, not yet applied/verified

`AddJwtBearer` remaps the `sub` claim to the legacy `ClaimTypes.NameIdentifier` URI on validation by default. Every
read of the user id (`TenantMiddleware`, `GET /api/v1/auth/me`) looks for the short claim name `sub`
(`JwtRegisteredClaimNames.Sub`), which `TokenService` writes under that same short name — so after validation the
lookup finds nothing, and every authenticated request behaves as if it were anonymous (tenant never resolves →
"No workspace was selected" on every permission-checked endpoint; `/me` returns 401).

**The fix is written and compiles**: `o.MapInboundClaims = false;` added to the `AddJwtBearer` options in
`services/gateway/Daksh.Gateway/Program.cs` (right before `TokenValidationParameters`). `dotnet build Daksh.sln`
is clean with it in place. **It has not been re-verified by running the scenario again** — that's the very next
step.

## Exact next steps, in order

1. Start Postgres and the Gateway, re-run the end-to-end script, confirm `/me` now returns 200 and the rest of the
   scenario (provider setup → agent → chat → tool calls → memory recall → scheduling) passes:
   ```bash
   docker compose up -d postgres   # POSTGRES_PORT=5440 is set in .env on this machine; port 5432 was taken by another local service
   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5190 \
     Database__MigrateOnStartup=true \
     ConnectionStrings__Platform="Host=localhost;Port=5440;Database=daksh_platform;Username=daksh;Password=change-me" \
     dotnet run --project services/gateway/Daksh.Gateway
   python3 -I /private/tmp/claude-501/-Volumes---------Projects-amitnahaksvn-Civix-LiveStream/f767c5e3-7375-4c1f-be7f-bc83e8fa46a2/scratchpad/e2e_scenario.py
   ```
   (That scratchpad script is session-local; move it into `tests/` as a real Playwright/HTTP end-to-end test once
   it passes — see TST-005 in the parity matrix.)
2. Finish the required 16-step scenario: steps 13–16 (restart the Gateway mid-schedule, confirm the job still
   fires via `JobRunnerWorker`'s catch-up poll, confirm delivery through the mock channel, confirm usage/cost/logs
   are queryable) are written into the engine and worker but not yet exercised end-to-end.
3. Write the automated test projects' actual test classes — `tests/platform/Daksh.Platform.Tests` and
   `tests/gateway/Daksh.Gateway.Tests` currently have `.csproj` files only, no test code yet. Priority order:
   security tests (tenant isolation, path jail, SSRF guard, command policy — these are pure functions, easy and
   high value), `ToolPolicy.Evaluate`, `ScheduleCalculator`, the scripted-provider research behaviour, then
   `Testcontainers.PostgreSql`-backed integration tests for `PlatformDbContext` tenant filters and `MemoryService`.
4. Update `docs/parity/status.tsv` as each feature above is actually verified by a test (not just "written"), then
   run `python3 scripts/parity.py` to regenerate `docs/FEATURE_PARITY_MATRIX.md`. Right now only `LEG-001` (legacy
   tests still pass) is marked verified; everything else built above is still "Not implemented" in the matrix
   because it has no test evidence yet — that's intentionally honest, not a backlog of documentation debt.
5. Then continue down `docs/IMPLEMENTATION_ROADMAP.md` from Phase 5 (sandbox/approval hardening tests) onward.

## Decisions made worth knowing about

All recorded with rationale in `docs/TECHNICAL_DECISIONS.md` (TD-001 through TD-014); the two most likely to
surprise a reviewer:
- **Port 5432 was already taken** on this development machine by another project's Postgres. `docker-compose.yml`
  now reads `POSTGRES_PORT` (default 5432, unchanged for anyone else); this machine's `.env` sets it to 5440.
- `Microsoft.AspNetCore.SignalR.StackExchangeRedis` has a known DoS advisory (CVE-2026-114147) fixed only in
  11.0.0, which requires net11.0 and cannot be referenced from this net10.0 solution. Pinned to 10.0.13 with
  `NoWarn="NU1903"` and a comment in `Daksh.Gateway.csproj`; it's only loaded when Redis scale-out is enabled
  (off by default). Revisit when a net10.0-compatible patch ships.

## What is explicitly not done yet (not hidden, just not reached)

Everything the parity matrix lists as "Not implemented": real channels beyond the mock (Telegram/Discord/Slack/
WhatsApp), skills/plugins management UI and catalog, voice/STT/TTS, media generation, device pairing endpoints on
the Gateway (the reference node's protocol is written but has nothing to pair with yet), the Control UI (no
`apps/control` has been created), most of the CLI's commands (only `gateway status`, `health`, `agents list`,
`automations list`, `usage` exist), Docker Compose entries for the Gateway/Redis/RabbitMQ/Control UI (only
Postgres is in `docker-compose.yml` so far), CI changes, and the full test suites. None of this was skipped
silently — it's listed in the roadmap and the matrix as not started.
