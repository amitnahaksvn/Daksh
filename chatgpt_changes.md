# Implementation status — Daksh agent platform

Last updated: 2026-11-12. This is the resume point for a cold session with no memory of prior conversations.
Read this file fully, then `docs/IMPLEMENTATION_ROADMAP.md` and `docs/FEATURE_PARITY_MATRIX.md`, before writing
any code. Nothing in here depends on a session-specific path, a scratchpad, or anything outside this repository —
everything needed to resume is either in this file or in the paths it names below.

**This content is kept in sync, byte-for-byte, with `IMPLEMENTATION_STATUS.md` at the repo root.** Both exist
because the person running this asked for a file literally named `chatgpt_changes.md`; update both together.

## Current git state

On `main`, already merged and pushed (commit `1766d74`, "Add Daksh agent platform foundation (OpenClaw-inspired)
+ bento web theme"). Nothing is pending in a branch or stash. The next work should branch off `main`.

## Where things stand

Phase 0 (audit) is complete. Phases 1–4 (foundation, identity, model providers, Gateway + agent runtime) are
built and the whole solution compiles: `dotnet build Daksh.sln` → 0 errors, 0 warnings, including the untouched
legacy `Daksh.Api`/`Daksh.Worker`/`Daksh.Ai` projects. Phases 5–15 (sandbox hardening, memory polish, multi-agent,
channels beyond the mock, skills, plugins beyond the example, voice/media, devices, Control UI, CLI completeness,
hardening, final verification) are **not started** beyond what's noted below.

## What is built and compiles

- **Domain model** (`services/platform/Daksh.Platform.Core/Domain`): ~55 entities covering identity, workspaces,
  roles/permissions, agents, providers/models, sessions/messages/executions, tools/approvals, memory, skills,
  plugins, channels, automation, devices, files, usage/cost, audit.
- **Security primitives** (`.../Security/Crypto.cs`, `Guards.cs`): token/password hashing, AES-GCM secret
  envelope with key rotation, TOTP, a secret redactor, a workspace-jailed path resolver that follows symlinks
  before deciding, an SSRF guard that resolves DNS itself and checks the address actually connected to, a
  deny-pattern command policy, and the untrusted-content wrapper used everywhere outside content reaches a model.
- **Tool framework** (`.../Tools/Contracts.cs`): `ITool`, JSON-schema validation, tool groups/profiles, and the
  full allow/alsoAllow/deny/approve policy evaluator (deny always wins). A tool can declare itself `Unavailable`
  (e.g. no API key configured); unavailable tools are never offered to the model — see the open gap below.
- **Model providers** (`.../Providers/*`): OpenAI, Anthropic, Ollama adapters against the real wire formats
  (streaming, tool calls, vision, embeddings), and a **scripted provider** with two behaviours — `echo` and a
  rule-based `research` behaviour meant to drive the required end-to-end scenario deterministically and offline
  (see "Known gap" below — it currently has nothing to call). `ModelCallRunner` (in `Daksh.Platform`) resolves
  "provider/model" refs, rotates credentials on auth/rate-limit failures with growing cooldowns, and records
  usage/cost.
- **Agent runtime** (`Daksh.Platform/Runtime/AgentRuntimeEngine.cs`): the full step loop — prompt building with
  instructions + memory recall + history, streaming, tool-call execution with policy/approval/risk checks, loop
  detection, context compaction via a summarizing model call, usage/cost recording. `RuntimeWorker` claims queued
  or lease-expired executions (SKIP-LOCKED-style, lease renewal in the background) so a restart resumes work
  instead of losing it. **Verified working end-to-end** (see below): register → create agent → create session →
  send message → execution runs to `Succeeded` and returns a real answer.
- **Memory** (`Daksh.Platform/Memory/MemoryService.cs`): pgvector cosine search fused (reciprocal-rank) with
  PostgreSQL full-text search; falls back to full-text alone with no embedding model configured; dedupe by
  normalized-content hash. Not yet exercised end-to-end (see gap below — nothing has called `memory_write` yet).
- **Automation** (`Daksh.Platform/Automation/*`): `ScheduleCalculator` (at/every/cron, IANA time zones, retry
  backoff) and `JobRunnerWorker`, which claims due jobs with `FOR UPDATE SKIP LOCKED` and runs the agent prompt
  action through the same runtime engine. Not yet exercised end-to-end.
- **Channels**: `IChannelAdapter` contract, DM/group access policy evaluated in code, and a `MockChannelAdapter` +
  `ChannelService` + `ChannelOutboxWorker` sufficient for the required scenario's "deliver through a channel" step
  and for automated channel tests. No real channel (Telegram/Discord/Slack/WhatsApp) is wired yet.
- **Sandbox**: a container backend (Docker, no network, resource limits — the real boundary) and a process
  backend (explicitly documented as not a boundary), selected per agent and capped by an instance-wide ceiling.
- **Identity/workspaces**: register/login/refresh-with-reuse-detection/MFA scaffolding, six built-in roles with a
  real permission set, tenant isolation enforced in `PlatformDbContext` (query filters + a save-time guard that
  rejects a write into another workspace even if a check upstream was missed). **Fully verified working**
  (see below).
- **Gateway** (`services/gateway/Daksh.Gateway`): ASP.NET Core host, JWT auth, SignalR hub for live run events,
  REST modules for auth/workspaces/providers/agents/sessions/approvals/automations/usage/mock-channel testing,
  OpenTelemetry, Serilog, health checks, OpenAPI.
- **CLI, reference desktop node, example plugin**: minimal but real — the CLI talks to the Gateway's REST API,
  the node demonstrates the pairing/SignalR protocol (pairing endpoints on the Gateway side are not built yet,
  so it cannot complete a real pairing today), the plugin runs the full stdio JSON-RPC lifecycle.
- **Database**: one EF Core migration (`InitialCreate`) applied cleanly to real PostgreSQL 17 with pgvector
  (58 tables; verified with `\dx` and `\d` against the running container). `pgvector` extension creation and the
  unmapped `vector` column are added as raw SQL inside the migration (see TD-012 in `docs/TECHNICAL_DECISIONS.md`).
- **Manual end-to-end script**: `tests/manual/e2e_scenario.py` — a real, runnable (not hypothetical) script that
  drives the required 16-step scenario via plain `urllib` HTTP calls against a running Gateway. It registers a
  fresh user each run (timestamped email), so it's safe to re-run against the same database. This is a durable
  repo file now, not a scratchpad artifact — run it any time with Python 3's standard library only, no deps.

## Verified by actually running it, this session (2026-11-12)

Previously (2026-11-11) a JWT bug was found and a fix wreitten but not re-verified. **It has now been verified
end to end**, against a live `pgvector/pgvector:pg17` Postgres container and the real built Gateway binary:

1. `docker compose up -d postgres` (reads `POSTGRES_PORT` from `.env`, 5440 on this machine) → container healthy.
2. `dotnet build Daksh.sln -c Release` → 0 errors.
3. Gateway started against that database with `Database__MigrateOnStartup=true` → migration applied (idempotent;
   the `daksh_platform` database already existed from a prior run), `/health/ready` → `Healthy`.
4. `python3 -I tests/manual/e2e_scenario.py` run against it:
   - Step 1 (register/login): **HTTP 200**, issues a workspace with all six built-in roles and the owner's full
     permission set.
   - `GET /api/v1/auth/me` with the issued token: **HTTP 200** (previously 401 — this is the bug that's now
     confirmed fixed: `o.MapInboundClaims = false;` on the JWT bearer options in
     `services/gateway/Daksh.Gateway/Program.cs`).
   - Step 3 (configure scripted provider + two models): **HTTP 200** each.
   - Step 2 (create research agent): **HTTP 200**.
   - Step 4 (create session, send a research request): **HTTP 200** / **HTTP 202**.
   - The execution it started reached status **`Succeeded`** (not stuck, not failed) and returned a real,
     non-error answer. `RuntimeWorker` correctly picked up the queued execution and ran it to completion.

So: **registration, authentication, tenant/workspace resolution, provider/model configuration, agent creation,
session creation, and the full execution lifecycle (queued → running → succeeded) are all confirmed working
end to end against a real database and a real running process.** This is the single most important thing fixed
and verified this session — the platform's core request path works.

## Known gap found by this same run (not a regression, a real missing piece)

Step "5-8" (agent searches the web, fetches a page, summarizes, saves to memory) **did not call any tools**. The
scripted `research` model behaviour fell through to its default branch and just echoed the user's message back,
because **no tool in the "web" group was actually offered to the model**:

- `WebSearchTool.Unavailable(services)` returns a non-null reason whenever no web search backend is enabled
  (`Tools:WebSearch:Provider` defaults to `"none"` in `appsettings.json`), and `AgentRuntimeEngine.BuildToolSpecsAsync`
  correctly filters out any tool whose `Unavailable` check fails — so the model is never given `web_search` or
  `web_fetch` to call, regardless of the agent's tool profile.
- With those unavailable, the scripted `ResearchBehavior` in `ScriptedProvider.cs` never sees `Has(request,
  "web_search")` return true, so it never calls `web_search` → never calls `web_fetch` → never calls
  `memory_write`. The follow-up/recall step and the scheduling step were **not reached** in this run because the
  manual test script's assertion on tool names stops it there by design (so a silent "it looks fine but nothing
  really happened" pass can't occur).

This is **not a bug in the policy/approval/execution machinery** — it's working exactly as designed (don't offer
a tool the platform can't actually run). It is a **missing piece for offline/CI testing specifically**: there is
no way today to exercise `web_search`/`web_fetch` without a real external search provider (SearXNG, Brave,
Tavily) configured, which defeats the point of the scripted provider being usable without credentials.

**The fix, not yet built**: add a fourth `IWebSearchBackend` (e.g. `ScriptedSearchBackend`) alongside
`SearXngSearchBackend`/`BraveSearchBackend`/`TavilySearchBackend` in
`services/platform/Daksh.Platform/Tools/WebTools.cs`, enabled when `Tools:WebSearch:Provider == "scripted"`,
returning a small fixed/deterministic result set so `WebSearchTool.Unavailable` returns null and the scripted
`research` behaviour's whole call chain (`web_search` → `web_fetch` → `memory_write` → `memory_search` →
`schedule_create`) can actually run in tests and in this manual script. `web_fetch` itself has no such gap — it's
unconditionally available (it has no "no backend configured" state) — so once `web_search` can return a result,
`web_fetch`, `memory_write` and the rest of the chain should work without further changes, but that is **not
verified** and is exactly the next thing to check once the scripted search backend exists.

## Exact next steps, in order

1. Build the `ScriptedSearchBackend` described above (small, self-contained, no new dependencies) and set
   `Tools:WebSearch:Provider=scripted` in `appsettings.Development.json` (or via `Tools__WebSearch__Provider=scripted`
   env var) for local/offline runs.
2. Re-run `python3 -I tests/manual/e2e_scenario.py` (same commands as in "Verified" above) and confirm it now
   gets all the way through: web_search → web_fetch → memory_write (steps 5–8), the follow-up/memory_search step
   (9–10), and the scheduling step (11–12). The script will tell you exactly which assertion fails if any step
   still doesn't behave as expected — read its output, don't guess.
3. Steps 13–16 of the required scenario (restart the Gateway mid-schedule, confirm the job still fires via
   `JobRunnerWorker`'s catch-up poll, confirm delivery through the mock channel, confirm usage/cost/logs are
   queryable) are written into the engine and worker but **have never been exercised**, automated or manual. Add
   that to `tests/manual/e2e_scenario.py` (it already prints an automation id to extend from) or a new script.
4. Turn `tests/manual/e2e_scenario.py` into a real automated test once it fully passes (currently it's a manual
   script you run by hand against a Gateway you started yourself) — move its logic into
   `tests/gateway/Daksh.Gateway.Tests` using `Microsoft.AspNetCore.Mvc.Testing` / `Testcontainers.PostgreSql` so
   it runs under `dotnet test` without a hand-started Gateway. This is TST-005 in the parity matrix.
5. Write the rest of the automated test projects' actual test classes — `tests/platform/Daksh.Platform.Tests`
   and `tests/gateway/Daksh.Gateway.Tests` currently have `.csproj` files only, no test code yet beyond what #4
   produces. Priority order: security tests (tenant isolation, path jail, SSRF guard, command policy — pure
   functions, easy and high value), `ToolPolicy.Evaluate`, `ScheduleCalculator`, the scripted-provider research
   behaviour, then `Testcontainers.PostgreSql`-backed integration tests for `PlatformDbContext` tenant filters
   and `MemoryService`.
6. Update `docs/parity/status.tsv` as each feature above is actually verified by a test (not just "written"),
   then run `python3 scripts/parity.py` to regenerate `docs/FEATURE_PARITY_MATRIX.md`. Right now only `LEG-001`
   (legacy tests still pass) is marked verified; everything built this session is still "Not implemented" in the
   matrix because it has no automated-test evidence yet — a passing manual script is real progress but is
   intentionally not enough to mark a matrix row verified.
7. Then continue down `docs/IMPLEMENTATION_ROADMAP.md` from Phase 5 (sandbox/approval hardening tests) onward.

## How to resume, concretely (copy-pasteable)

```bash
cd "/Volumes/रत्नकोष/Projects/amitnahaksvn/Daksh/Daksh"
git status   # should be clean, on main, at or after commit 1766d74

# 1. Start Postgres (pgvector image; POSTGRES_PORT=5440 is set in .env on this machine because 5432 is
#    already used by another local project — see docs/TECHNICAL_DECISIONS.md TD-013).
docker compose up -d postgres

# 2. Build.
dotnet build Daksh.sln -c Release

# 3. Run the Gateway against that database (first run creates/migrates daksh_platform automatically).
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5190 \
  Database__MigrateOnStartup=true \
  ConnectionStrings__Platform="Host=localhost;Port=5440;Database=daksh_platform;Username=daksh;Password=change-me" \
  dotnet run --project services/gateway/Daksh.Gateway -c Release --no-build

# 4. In another terminal, confirm health and run the manual scenario.
curl -s http://localhost:5190/health/ready
python3 -I tests/manual/e2e_scenario.py
```

If step 4 fails, the script's own `[FAIL]` line and the printed response body tell you exactly which HTTP call
and assertion broke — start there, not by re-deriving the whole flow.

## Decisions made worth knowing about

All recorded with rationale in `docs/TECHNICAL_DECISIONS.md` (TD-001 through TD-015); the three most likely to
surprise a reviewer:
- **Port 5432 was already taken** on this development machine by another project's Postgres. `docker-compose.yml`
  now reads `POSTGRES_PORT` (default 5432, unchanged for anyone else); this machine's `.env` sets it to 5440.
- `Microsoft.AspNetCore.SignalR.StackExchangeRedis` has a known DoS advisory (CVE-2026-114147) fixed only in
  11.0.0, which requires net11.0 and cannot be referenced from this net10.0 solution. Pinned to 10.0.13 with
  `NoWarn="NU1903"` and a comment in `Daksh.Gateway.csproj`; it's only loaded when Redis scale-out is enabled
  (off by default). Revisit when a net10.0-compatible patch ships.
- **JWT inbound claim mapping must stay off** (`o.MapInboundClaims = false` in `Daksh.Gateway/Program.cs`).
  Without it, ASP.NET Core's JWT bearer handler silently renames the `sub` claim to the legacy
  `ClaimTypes.NameIdentifier` URI on validation, which no longer matches what `TokenService` writes and what
  `TenantMiddleware`/`/auth/me` read — this was the whole-platform-unusable-past-login bug found and fixed in the
  previous session and reconfirmed working in this one. If this setting is ever removed "to simplify," every
  authenticated request will silently stop resolving a workspace again.

## What is explicitly not done yet (not hidden, just not reached)

Everything the parity matrix lists as "Not implemented": real channels beyond the mock (Telegram/Discord/Slack/
WhatsApp), skills/plugins management UI and catalog, voice/STT/TTS, media generation, device pairing endpoints on
the Gateway (the reference node's protocol is written but has nothing to pair with yet), the Control UI (no
`apps/control` has been created), most of the CLI's commands (only `gateway status`, `health`, `agents list`,
`automations list`, `usage` exist), Docker Compose entries for the Gateway/Redis/RabbitMQ/Control UI (only
Postgres is in `docker-compose.yml` so far), CI changes, and the full automated test suites. None of this was
skipped silently — it's listed in the roadmap and the matrix as not started.
