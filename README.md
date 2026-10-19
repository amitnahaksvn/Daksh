# Daksh

Daksh is a personal AI operator platform: an assistant that manages conversations, tasks, reminders, email and calendar,
under the user's control. This monorepo holds every component.

| Component | Path | Tech |
|-----------|------|------|
| Web | `apps/web` | Next.js, TypeScript |
| Mobile (iOS/Android) | `apps/mobile` | Flutter |
| API | `services/api` | ASP.NET Core, .NET 10 (LTS) |
| Worker | `services/worker` | .NET Worker Service |
| AI core (LLM/RAG/ML orchestration) | `services/ai` | .NET, Microsoft.Extensions.AI |
| Integrations (adapters) | `services/integrations` | .NET |
| Shared domain/data | `services/shared` | EF Core, PostgreSQL |

Tests live in `tests/` (plus colocated web/mobile unit tests), infrastructure in `infrastructure/`, docs in `docs/`.

> **Status: foundation.** Everything external (AI providers, email, calendar, push, n8n, identity) runs on **mock adapters**
> and is reported as `Mock` by `GET /api/v1/integrations`. Nothing is claimed operational until verified.
> The Daksh Master Prompt is **not** in this repository yet; see `docs/REQUIREMENTS_TRACKER.md`.

## Prerequisites

.NET SDK 10, Node.js 22+, Docker (for PostgreSQL), Flutter stable (mobile only).

## Quick start

```bash
cp .env.example .env
docker compose up -d postgres
./scripts/dev.sh                      # API on http://localhost:5080 (applies migrations in Development)
dotnet run --project services/worker/Daksh.Worker   # in another terminal
cd apps/web && cp .env.example .env.local && npm ci && npm run dev    # http://localhost:3000
cd apps/mobile && flutter run --dart-define=API_URL=http://10.0.2.2:5080 --dart-define=DEV_USER=dev-user
./scripts/test.sh                     # all tests
```

Full outstanding-work checklist: [docs/ROADMAP.md](docs/ROADMAP.md). Next steps are listed in [docs/REQUIREMENTS_TRACKER.md](docs/REQUIREMENTS_TRACKER.md#next-actions).

Docs: [Architecture](docs/ARCHITECTURE.md) · [AI architecture](docs/AI_ARCHITECTURE.md) · [Setup](docs/SETUP.md) ·
[API](docs/API.md) · [Mobile platform limits](docs/MOBILE_PLATFORM_LIMITS.md) · [Requirements](docs/REQUIREMENTS_TRACKER.md)
