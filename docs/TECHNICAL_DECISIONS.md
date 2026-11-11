# Technical decisions

Each entry: the decision, why, and what it costs. Add new entries at the bottom; do not rewrite old ones, supersede them.

## TD-001 The platform is added to the Daksh monorepo beside the existing services
The repository already holds a personal-assistant foundation (`Daksh.Api`, `Daksh.Worker`, `Daksh.Ai`,
`Daksh.Integrations`, `Daksh.Domain`, a Next.js site and a Flutter app). The master prompt
(`docs/Daksh_Master_Prompt.md`) says to preserve working code. The agent platform is therefore built as **new projects**
and the existing ones are left compiling, tested and unchanged. Their features (tasks, reminders, email, calendar) are
to be folded into the platform as tools in a later step (roadmap Phase 16). Cost: two API hosts exist until then.

## TD-002 Existing folder layout is kept (`services/`, `apps/`, `tests/`, `infrastructure/`)
The master prompt allows adapting its suggested `src/Backend/...` tree to an established layout. New code goes to
`services/platform/*`, `services/gateway`, `services/cli`, `services/node`, `apps/control`, `plugins/`, `skills/`.

## TD-003 Modular monolith in three assemblies
- `Daksh.Platform.Core`: entities, contracts and pure logic (policies, guards, parsers, provider adapters over
  `HttpClient`, scheduling maths). No EF Core, no ASP.NET.
- `Daksh.Platform`: EF Core `PlatformDbContext`, migrations, module services, the agent runtime, hosted workers, Redis
  and RabbitMQ adapters.
- `Daksh.Gateway`: ASP.NET Core host (REST, SignalR, auth, middleware).
Modules are folders/namespaces (`Modules/Identity`, `Modules/Agents`, ...), not separate assemblies: boundaries are
enforced by convention and by `Core` having no infrastructure references. Cost: no compiler-enforced module isolation
inside `Daksh.Platform`.

## TD-004 Separate `platform` schema and migration history
`PlatformDbContext` uses PostgreSQL schema `platform` with its own `__EFMigrationsHistory`, so it can share a server (or
a database) with the legacy `DakshDbContext` without the two migration sets touching each other.

## TD-005 Own provider abstraction instead of `Microsoft.Extensions.AI`
The legacy `Daksh.Ai` uses `IChatClient`. The platform needs per-model capability metadata, raw streaming tool-call
deltas, provider-specific options, usage/cost and deterministic test doubles at the HTTP level. Adapters are written
directly against the providers' HTTP APIs behind `IModelProvider`. Cost: we maintain wire formats ourselves.

## TD-006 Durable scheduler on PostgreSQL instead of Hangfire
The master prompt allows "Hangfire or an equivalent durable scheduling mechanism". Jobs are rows claimed with
`FOR UPDATE SKIP LOCKED`, a lease, and a unique `(automation, scheduled_for)` key. Reasons: tenant-scoped jobs live in
the same transaction as the automation, time zones and cron via `Cronos`, no second storage schema, and the same
mechanism runs agent executions. Cost: no Hangfire dashboard; the Control UI has its own.

## TD-007 Redis and RabbitMQ are optional at runtime
Single-user local deployment must work with PostgreSQL only. `IDistributedLock`, `ICacheStore` and `IEventBus` have
in-process/PostgreSQL defaults; Redis (locks, cache, rate-limit state, SignalR backplane) and RabbitMQ (event fan-out
between Gateway instances, dead-letter queue) are switched on by configuration and are part of the Docker Compose stack.

## TD-008 Built-in identity
Local accounts with PBKDF2 password hashes (ASP.NET Core `PasswordHasher`), short-lived JWT access tokens, rotating
hashed refresh tokens, TOTP MFA, hashed API keys. An external OIDC provider can be added later as another scheme. The
legacy API keeps its own auth setup.

## TD-009 Control UI is a new Vite app (`apps/control`); `apps/web` is not touched
The master prompt specifies React + Vite. `apps/web` (Next.js) has uncommitted work in progress by the owner and serves
the legacy assistant. The Control UI is a client-only SPA over REST + SignalR, which fits Vite. Cost: two web apps until
`apps/web` is retired or turned into the public site.

## TD-010 Shell, code and plugin execution never run in the Gateway process
`exec`, `process` and `code_execute` go through `ISandboxRunner`: a Docker backend (no network by default, memory/CPU/pid
limits, workspace bind mount) or a child-process backend (scrubbed environment, working-directory jail, timeout,
output cap) that is explicitly labelled "not a security boundary" and must be enabled by configuration. Plugins are
separate processes speaking JSON-RPC over stdio.

## TD-011 Execution state is persisted step by step
Every message, tool call and tool result is written before the next step starts; `ExecutionCheckpoints` holds the step
counter, usage totals and pending approvals. A restarted Gateway re-queues executions whose lease expired. A tool call
that was running at the crash is **not** re-run unless the tool is marked idempotent; the model is told it was
interrupted.

## TD-012 Vector column without a fixed dimension
Embedding dimensions depend on the configured provider. `memory_embeddings.embedding` is `vector` (no dimension) with
`model` and `dimensions` columns; search filters on the model and scans exactly. Cost: no HNSW/IVFFlat index, acceptable
for self-hosted volumes. A per-model partial index can be added later.

## TD-013 Ports
Gateway `5190`, Control UI dev server `5273` (5173 and 5080 are already in use on the development machine), PostgreSQL
host port from `.env` (`POSTGRES_PORT`).

## TD-014 No commits are made by the assistant without the owner's request
Work stays in the working tree; resumability comes from `IMPLEMENTATION_STATUS.md`. The owner's uncommitted changes in
`apps/web` are left as they are.
