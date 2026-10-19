# Architecture

## Components and boundaries

```
 Web (Next.js) ─┐                       ┌─> Daksh.Ai (orchestrator, routing, guardrails, memory, ML)
                ├─> Daksh.Api ──────────┤
 Mobile (Flutter)┘   (REST, OIDC)       └─> Daksh.Integrations (mock/real adapters behind interfaces)
                         │  writes jobs & audit events
                         ▼
                    PostgreSQL <──── Daksh.Worker (claims jobs, runs handlers, retries)
```

- **Clients** (web, mobile) contain no business logic; they call the API only (`docs/API.md`).
- **Daksh.Api** validates input, authenticates (OIDC JWT; fixed dev user in Development only), enforces per-user data
  isolation, persists state, writes **audit events in the same transaction** as the change, and enqueues jobs.
- **Daksh.Worker** is independently deployable. It polls the `Jobs` table, claims rows with an optimistic-concurrency
  token (safe with multiple replicas), runs `IJobHandler`s, retries with exponential backoff, dead-letters after
  `MaxAttempts`, and releases a job without consuming an attempt on shutdown. Delivery is at-least-once; handlers are
  idempotent (e.g. `Reminder.NotifiedAt`), and a unique `IdempotencyKey` prevents duplicate jobs.
- **Daksh.Ai** is the single place LLM/ML behaviour lives (see `AI_ARCHITECTURE.md`).
- **Daksh.Integrations** holds provider interfaces and mock adapters. Real providers replace mocks through DI only.
- **Daksh.Domain** (`services/shared`) holds entities, `DakshDbContext` and migrations, shared by API and worker.

Dependency direction: `Api, Worker -> Integrations -> Ai` and `Api, Worker -> Domain`. `Ai` depends on nothing internal.

## Data flow: reminder

1. Client `POST /api/v1/reminders` -> validated -> `Reminder` + `Job(reminder.due, RunAt)` + audit event saved atomically.
2. Worker claims the job at `RunAt`, sends a push via `IPushNotificationSender`, marks the reminder notified, audits it.

## Data flow: assistant message

Client -> API -> load user's privacy settings -> `IAssistantOrchestrator` -> model reply -> save messages + audit event -> client.

## Deployment

Containers for API and worker (`infrastructure/docker`), PostgreSQL, static/Node hosting for web, store builds for mobile.
Targets are undecided (REQ-INF-004); `infrastructure/deployment` contains an unverified template.

## Observability

Structured JSON console logs; health endpoints `/health/live` and `/health/ready`; `Daksh.Ai` emits an `ActivitySource` and
`Meter` (requests, tokens per model) ready for OpenTelemetry export (not yet wired to a backend).
