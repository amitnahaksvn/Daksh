# Implementation status — Daksh agent platform

Last updated: 2026-11-12. This is the resume point for a cold session with no memory of prior conversations.
Read this file fully, then `docs/IMPLEMENTATION_ROADMAP.md` and `docs/FEATURE_PARITY_MATRIX.md`, before writing
any code. Nothing in here depends on a session-specific path, a scratchpad, or anything outside this repository —
everything needed to resume is either in this file or in the paths it names below.

**This content is kept in sync, byte-for-byte, with `IMPLEMENTATION_STATUS.md` at the repo root.** Both exist
because the person running this asked for a file literally named `chatgpt_changes.md`; update both together.

## Current git state

On `main`, pushed through this session's work. In order: `8b61c92` (unblock and verify the full 16-step
scenario), `7e20656` (fix two execution double-run races), `53d935a` (Phase 5 security/policy test suite, 124
tests), `600c686` (TST-005: automate the 16-step scenario under `dotnet test`). `docs/parity/status.tsv` and
the regenerated `docs/FEATURE_PARITY_MATRIX.md` (now 8 "Verified" rows, up from 1) are the next thing to commit
and push if they aren't already — check `git status` first.

## Where things stand

Phase 0 (audit) is complete. Phases 1–4 (foundation, identity, model providers, Gateway + agent runtime) are
built and the whole solution compiles: `dotnet build Daksh.sln` → 0 errors, 0 warnings, including the untouched
legacy `Daksh.Api`/`Daksh.Worker`/`Daksh.Ai` projects. Phases 5–15 (sandbox hardening, memory polish, multi-agent,
channels beyond the mock, skills, plugins beyond the example, voice/media, devices, Control UI, CLI completeness,
hardening, final verification) are **not started** beyond what's noted below.

**This session, in order: (1)** the full required 16-step end-to-end scenario now passes, for real, against a
live database and a live running Gateway — not just steps 1-12 as before — after finding and fixing five
separate bugs by actually running it; **(2)** two more real bugs causing double-execution of the same run
(`IX_Messages_SessionId_Seq` races) were found and fixed; **(3)** a 124-test Phase 5 security/policy suite was
added to `Daksh.Platform.Tests` (previously empty); **(4)** the 16-step scenario was converted from manual
Python scripts into real automated tests under `dotnet test` (TST-005), using `Testcontainers.PostgreSql`; and
**(5)** `docs/parity/status.tsv` / `FEATURE_PARITY_MATRIX.md` were updated accordingly — 8 rows now "Verified"
with real automated-test evidence, up from 1.

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
  (e.g. no API key configured); unavailable tools are never offered to the model.
- **Model providers** (`.../Providers/*`): OpenAI, Anthropic, Ollama adapters against the real wire formats
  (streaming, tool calls, vision, embeddings), and a **scripted provider** with two behaviours — `echo` and a
  rule-based `research` behaviour that drives the required end-to-end scenario deterministically and offline.
  `ModelCallRunner` (in `Daksh.Platform`) resolves "provider/model" refs, rotates credentials on auth/rate-limit
  failures with growing cooldowns, and records usage/cost.
- **Web tools, now with an offline backend** (`.../Tools/WebTools.cs`): `web_search`/`web_fetch`/`http_request`,
  plus `SearXngSearchBackend`/`BraveSearchBackend`/`TavilySearchBackend` and (new this session) a
  **`ScriptedSearchBackend`** that returns a small fixed result (pointing at `https://example.com/`) when
  `Tools:WebSearch:Provider=scripted`, so the scripted `research` behaviour's whole tool chain can run without
  any real search provider configured. `web_fetch` then does a real (small, public, deterministic) HTTP fetch —
  this only works with actual network access from the machine running the Gateway; there is no fully-offline
  path yet if that's ever needed (not required for anything today).
- **Agent runtime** (`Daksh.Platform/Runtime/AgentRuntimeEngine.cs`): the full step loop — prompt building with
  instructions + memory recall + history, streaming, tool-call execution with policy/approval/risk checks, loop
  detection, context compaction via a summarizing model call, usage/cost recording. `RuntimeWorker` claims queued
  or lease-expired executions (SKIP-LOCKED-style, lease renewal in the background) so a restart resumes work
  instead of losing it. **Verified working end-to-end, including channel delivery** (see below).
- **Memory** (`Daksh.Platform/Memory/MemoryService.cs`): pgvector cosine search fused (reciprocal-rank) with
  PostgreSQL full-text search; falls back to full-text alone with no embedding model configured; dedupe by
  normalized-content hash. **Fixed this session** (see below) and now verified working end-to-end: `memory_write`
  then a later natural-language recall question via `memory_search` actually finds it.
- **Automation** (`Daksh.Platform/Automation/*`): `ScheduleCalculator` (at/every/cron, IANA time zones, retry
  backoff) and `JobRunnerWorker`, which claims due jobs with `FOR UPDATE SKIP LOCKED` and runs the agent prompt
  action through the same runtime engine. **Verified working end-to-end this session, including restart
  recovery**: a job scheduled while the Gateway is up still runs correctly if the Gateway is killed and
  restarted after the job's due time passes (see below).
- **Channels**: `IChannelAdapter` contract, DM/group access policy evaluated in code, and a `MockChannelAdapter` +
  `ChannelService` + `ChannelOutboxWorker`. **Fixed this session** (see below) and now verified working
  end-to-end: an inbound message routed to an agent produces a real reply delivered back out through the
  channel. No real channel (Telegram/Discord/Slack/WhatsApp) is wired yet.
- **Sandbox**: a container backend (Docker, no network, resource limits — the real boundary) and a process
  backend (explicitly documented as not a boundary), selected per agent and capped by an instance-wide ceiling.
- **Identity/workspaces**: register/login/refresh-with-reuse-detection/MFA scaffolding, six built-in roles with a
  real permission set, tenant isolation enforced in `PlatformDbContext` (query filters + a save-time guard that
  rejects a write into another workspace even if a check upstream was missed). Fully verified working.
- **Gateway** (`services/gateway/Daksh.Gateway`): ASP.NET Core host, JWT auth, SignalR hub for live run events,
  REST modules for auth/workspaces/providers/agents/sessions/approvals/automations/usage/mock-channel testing,
  OpenTelemetry, Serilog, health checks, OpenAPI.
- **CLI, reference desktop node, example plugin**: minimal but real — the CLI talks to the Gateway's REST API,
  the node demonstrates the pairing/SignalR protocol (pairing endpoints on the Gateway side are not built yet,
  so it cannot complete a real pairing today), the plugin runs the full stdio JSON-RPC lifecycle.
- **Database**: one EF Core migration (`InitialCreate`) applied cleanly to real PostgreSQL 17 with pgvector
  (58 tables; verified with `\dx` and `\d` against the running container). `pgvector` extension creation and the
  unmapped `vector` column are added as raw SQL inside the migration (see TD-012 in `docs/TECHNICAL_DECISIONS.md`).
- **Manual end-to-end scripts**: `tests/manual/e2e_scenario.py` drives steps 1-14 (register → research →
  recall → schedule → channel delivery → usage queryable) via plain `urllib` HTTP calls against a running
  Gateway; it registers a fresh user each run, so it's safe to re-run against the same database.
  `tests/manual/e2e_restart_recovery.py` (new this session) drives steps 15-16 (restart mid-schedule, catch-up
  poll) as a two-phase script (`schedule` then `verify`), because that one genuinely needs an external process
  restart between the two halves — see "How to resume" below for the exact commands. Both are durable repo
  files, not scratchpad artifacts.

## Verified by actually running it, this session (2026-11-12)

Starting point: the previous session had fixed and verified a JWT bug (steps 1-4 of the scenario: register,
login, provider/model/agent/session setup, execution lifecycle) but found that steps 5-16 couldn't run at all,
because no tool in the "web" group was ever offered to the scripted model (`Tools:WebSearch:Provider` defaults
to `"none"`, so `WebSearchTool.Unavailable` always failed and the tool was filtered out). This session fixed that
and then found and fixed four more real bugs by actually running the rest of the scenario, in this order:

1. **Built `ScriptedSearchBackend`** (`.../Tools/WebTools.cs`) and set
   `Tools:WebSearch:Provider=scripted` in `appsettings.Development.json`, exactly as the previous session's
   "exact next steps" said to. This unblocked `web_search` → `web_fetch` → `memory_write` (steps 5-8): **verified
   with the real built Gateway binary against a live `pgvector/pgvector:pg17` container**, execution reaches
   `Succeeded` with a real summarized answer.
2. **Found and fixed a regex-ordering bug in `ResearchBehavior`**
   (`Daksh.Platform.Core/Providers/ScriptedProvider.cs`): the recall check (`RecallIntent`) was being tested
   *after* the research check (`ResearchIntent`), but `ResearchIntent` matches the phrase "find out about" —
   which a natural follow-up question like "What did you find out about X earlier?" contains — so every
   follow-up re-triggered a brand new web search instead of a memory recall. Swapped the order (recall checked
   first). This is the actual reason step 9-10 wasn't reachable, not a limitation of the scripted provider.
3. **Found and fixed a real functional bug in `MemoryService.SearchAsync`**: the full-text search used
   `plainto_tsquery`, which ANDs every word in the query, so a multi-word natural-language recall question
   would only ever match a memory containing every one of those words verbatim — which essentially never
   happens. Added `BuildOrTsQuery` (OR's the query's words together) and switched to `EF.Functions.ToTsQuery`
   with that. Verified: the same follow-up question now actually finds and returns the earlier research memory,
   not "I have nothing stored about that yet."
4. **Found and fixed a missing DI registration**: `MockChannelAdapter` was registered only as `IChannelAdapter`
   in `ServiceCollectionExtensions.cs`, but the mock-channel test endpoint
   (`GET /api/v1/testing/mock-channel/accounts/{id}/outbound`) asks for the concrete `MockChannelAdapter` type
   directly, which had no registration — every call 500'd with "No service for type ... has been registered."
   Fixed by registering the concrete type as a singleton and forwarding `IChannelAdapter` to it, so both
   resolutions share the same instance (and its in-memory sent-message list).
5. **Found and fixed a real missing feature, not a wiring bug**: `AgentRuntimeEngine` never created an outbound
   `ChannelMessage` when an execution for a channel-backed session succeeded — so `ChannelOutboxWorker` (which
   only ever looks for already-queued `Direction="out"` rows) had nothing to deliver, ever, for any channel.
   The required scenario's "deliver through a channel" step was simply not reachable before this fix, for *any*
   channel, not just the mock one. Fixed in `AgentRuntimeEngine.FinishAsync`: on a successful finish, if the
   session has a `ChannelAccountId`, it now inserts a pending outbound `ChannelMessage` with the final text,
   which `ChannelOutboxWorker` then picks up and delivers through the adapter within its normal poll interval.

After all five fixes, **every one of the 16 required scenario steps is confirmed working end to end**, against
a real database and a real running Gateway process, split across two scripts because step 15 genuinely needs an
external Gateway restart between two halves:

- `python3 -I tests/manual/e2e_scenario.py` → all of steps 1-14 pass: register/login, provider/model/agent/session
  setup, `web_search`→`web_fetch`→`memory_write`, follow-up `memory_search` recall, scheduling a job, mock-channel
  inbound→reply→outbound delivery, and `GET /api/v1/usage/summary` showing real recorded calls/tokens.
- `python3 -I tests/manual/e2e_restart_recovery.py schedule`, then (externally) kill the Gateway, wait past the
  job's due time, restart it, then `python3 -I tests/manual/e2e_restart_recovery.py verify` → steps 15-16 pass:
  the automation's `lastRunAt` gets set and `consecutiveFailures` stays 0 even though the Gateway was down when
  the job was originally due, confirming `JobRunnerWorker`'s catch-up poll (it claims any `Pending` job whose
  `RunAfter` has passed, regardless of how long ago) actually works, not just in theory.
- `dotnet test Daksh.sln -c Release` → still 0 failures (23 legacy tests, same as `LEG-001`); nothing regressed.

## The `IX_Messages_SessionId_Seq` race: found, root-caused, and fixed

The gap noted above (duplicate-key errors in the Gateway log, not visibly breaking the scenario but real wasted
work) turned out to be **two separate bugs**, both now fixed, both verified by re-running the full scenario
seven times back-to-back (three plain runs, a restart-recovery cycle, three more plain runs) against one
Gateway process with zero `IX_Messages_SessionId_Seq` occurrences afterward (there were 15 before, across 5
distinct exception instances, in the same test sequence):

1. **`Execution.Version` was never a real concurrency token.** `RuntimeWorker.RunOneAsync`'s claim step has a
   comment reading "Optimistic claim: only proceed if we win the race to set status/lease", but
   `PlatformDbContext.OnModelCreating` had `e.Ignore(x => x.Version)` for `Execution` — so that optimistic check
   was dead code: `SaveChangesAsync` never threw `DbUpdateConcurrencyException`, because there was no
   concurrency token for Postgres to check. Compounding this, `RuntimeWorker.ClaimAndRunBatchAsync` fires
   `RunOneAsync` as fire-and-forget and only delays before the *next* poll if nothing was claimed - so a busy
   poll loop can run its next `SELECT` candidates query before a just-fired claim's `UPDATE ... SET Status =
   Running` has committed, re-selecting and re-running the same `Queued` row a second time. Fixed by changing
   `Execution`'s mapping to `e.Property(x => x.Version).IsRowVersion()` (same `uint` + Npgsql `xmin` pattern
   already used for `Agent`/`Approval`/`Automation`), so the claim's own optimistic check is finally real: the
   loser of that race gets `DbUpdateConcurrencyException` and returns immediately instead of also running the
   engine. No physical schema change was needed (`xmin` is a Postgres system column that already exists on
   every table), but EF Core still requires a migration to record the mapping change - see
   `20261112071622_ExecutionVersionConcurrencyToken.cs`. **Important**: the scaffolded migration initially tried
   to `AddColumn("xmin", ...)`, which fails against a real Postgres server because `xmin` is a reserved system
   column name - the committed migration has an intentionally empty `Up`/`Down`. If this migration is ever
   regenerated from scratch, re-empty it the same way; don't apply the scaffolded version verbatim.
2. **`JobRunnerWorker.RunActionAsync` never claimed the execution it created.** It called
   `ExecutionQueueService.StartAsync` (which leaves the row `Queued`) and then called `engine.RunAsync` on it
   *directly itself*, without ever flipping the row to `Running` first. That `Queued` row was just as visible to
   `RuntimeWorker`'s own poll loop as any chat-triggered execution, so **every automation-triggered run was
   guaranteed to be picked up and run twice** - once by `JobRunnerWorker` inline, once by `RuntimeWorker`'s
   normal claim path - independent of bug #1. This is the bug that was actually firing in the observed log (the
   failing stack traces were in `JobRunnerWorker.RunActionAsync` → `AgentRuntimeEngine.RecordUsageAsync`, not in
   `RuntimeWorker`'s own re-claim path). Fixed by making `RunActionAsync` claim the execution itself (same
   optimistic `Status = Running` + lease + `WorkerId` pattern as `RuntimeWorker.RunOneAsync`) before calling the
   engine; on the rare loss of that race it now polls for the winner's terminal status instead of also running
   the engine.

Fixing bug #1 also required hardening `RuntimeWorker.RenewLeaseLoopAsync`: once `Execution.Version` is a real
concurrency token, the lease-renewal loop's periodic `LeaseUntil` write (from its own separate `DbContext`
scope) can legitimately lose a race against the engine's own in-flight writes on a long-running execution. That
loop now catches `DbUpdateConcurrencyException` and just skips that renewal tick (harmless - the engine is
actively progressing either way) instead of letting the exception propagate out of a fire-and-forget task.

## Exact next steps, in order

Everything numbered 1-4 in the previous version of this file is now **done**: the working tree was committed
and pushed through this session (see "Current git state"), TST-005 landed (`Daksh.Gateway.Tests`:
`E2eScenarioTests`, `RestartRecoveryTests`, real `Testcontainers.PostgreSql`), the Phase 5 security/policy
suite landed (`Daksh.Platform.Tests`: `Security/GuardsTests.cs`, `Tools/ToolPolicyTests.cs`,
`Automation/ScheduleCalculatorTests.cs`, `Providers/ResearchBehaviorTests.cs`, 124 tests), and
`docs/parity/status.tsv`/`FEATURE_PARITY_MATRIX.md` were updated for the 7 newly-verified rows (`SB-002`,
`SEC-006`, `SEC-007`, `SEC-010`, `TL-002`, `AU-002`, `TST-005`) plus the pre-existing `LEG-001`. What's left:

1. **Commit and push `docs/parity/status.tsv` and `docs/FEATURE_PARITY_MATRIX.md`** if `git status` shows them
   uncommitted — check this first, it may already be done.
2. Add a dedicated regression test for the two claim-race bugs fixed this session (see
   `## The IX_Messages_SessionId_Seq race` below): a test that fires two concurrent claims at the same
   `Execution` row and asserts only one proceeds (`DbUpdateConcurrencyException` on the loser) would catch both
   bugs #1 and #2 there if either ever regresses. This wasn't written this session - the fix was verified by
   re-running the full scenario seven times and grepping logs, which is real evidence but not a permanent
   regression test. Also worth adding: `MemoryService.SearchAsync`'s OR-query behavior (`BuildOrTsQuery`) has
   no dedicated unit test yet either - it's only exercised indirectly through `E2eScenarioTests`.
3. `PlatformDbContext` tenant-isolation tests (`Testcontainers.PostgreSql`-backed) were explicitly deferred by
   the agent that wrote the Phase 5 suite, as lower priority than the items that landed; still open.
4. `web_fetch`/`http_request` tools' actual use of the SSRF guard (as opposed to the guard itself, which is
   tested) is only exercised indirectly via `E2eScenarioTests` - see the `Limitations` column for `SEC-010` in
   `docs/parity/status.tsv`.
5. Then continue down `docs/IMPLEMENTATION_ROADMAP.md` from Phase 5 (remaining items: exec-only-via-sandbox
   test, approval pause/resume test, browser tool test) onward.

## How to resume, concretely (copy-pasteable)

```bash
cd "/Volumes/रत्नकोष/Projects/amitnahaksvn/Daksh/Daksh"
git status   # should show the uncommitted fixes from this session until step 1 above is done

# 1. Start Postgres (pgvector image; POSTGRES_PORT=5440 is set in .env on this machine because 5432 is
#    already used by another local project — see docs/TECHNICAL_DECISIONS.md TD-013).
docker compose up -d postgres

# 2. Build.
dotnet build Daksh.sln -c Release

# 3. Run the Gateway against that database (first run creates/migrates daksh_platform automatically).
#    Tools:WebSearch:Provider=scripted is already set in appsettings.Development.json.
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5190 \
  Database__MigrateOnStartup=true \
  ConnectionStrings__Platform="Host=localhost;Port=5440;Database=daksh_platform;Username=daksh;Password=change-me" \
  dotnet run --project services/gateway/Daksh.Gateway -c Release --no-build

# 4. In another terminal, confirm health and run the scenario scripts.
curl -s http://localhost:5190/health/ready
python3 -I tests/manual/e2e_scenario.py

# 5. For restart recovery (needs the Gateway process from step 3 to actually be killed and restarted):
python3 -I tests/manual/e2e_restart_recovery.py schedule
#   ... now Ctrl-C the Gateway from step 3, wait a few seconds past the printed "next run" time, restart it
#   with the same command as step 3 ...
python3 -I tests/manual/e2e_restart_recovery.py verify
```

If any script step fails, its own `[FAIL]` line and the printed response body (or traceback) tell you exactly
which HTTP call and assertion broke — start there, not by re-deriving the whole flow. If a fix doesn't seem to
take effect after rebuilding, make sure the Gateway process was fully killed (`pgrep -f Daksh.Gateway` should
print nothing) *before* rebuilding and restarting — an old process can keep running against the pre-fix binary
otherwise and make a real fix look like it didn't work.

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
  `TenantMiddleware`/`/auth/me` read — this was the whole-platform-unusable-past-login bug found and fixed two
  sessions ago and reconfirmed working since. If this setting is ever removed "to simplify," every authenticated
  request will silently stop resolving a workspace again.

## What is explicitly not done yet (not hidden, just not reached)

Everything the parity matrix lists as "Not implemented": real channels beyond the mock (Telegram/Discord/Slack/
WhatsApp), skills/plugins management UI and catalog, voice/STT/TTS, media generation, device pairing endpoints on
the Gateway (the reference node's protocol is written but has nothing to pair with yet), the Control UI (no
`apps/control` has been created), most of the CLI's commands (only `gateway status`, `health`, `agents list`,
`automations list`, `usage` exist), Docker Compose entries for the Gateway/Redis/RabbitMQ/Control UI (only
Postgres is in `docker-compose.yml` so far), CI changes, and the full automated test suites. None of this was
skipped silently — it's listed in the roadmap and the matrix as not started.
