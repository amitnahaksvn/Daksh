# Deployment templates

Targets are not chosen yet (see REQ-INF-004). Each service is a stateless container built from
`infrastructure/docker/*.Dockerfile`:

| Component | Image | Notes |
|-----------|-------|-------|
| API | `api.Dockerfile` | Needs `ConnectionStrings__Daksh`, `Auth__Mode=Jwt`, `Auth__Authority`. Probes: `/health/live`, `/health/ready`. |
| Worker | `worker.Dockerfile` | Same connection string; scale horizontally (jobs are claimed with optimistic concurrency). |
| Web | `npm run build && npm start` in `apps/web` | Set `NEXT_PUBLIC_API_URL`. |
| Mobile | Flutter store builds | `--dart-define=API_URL=...` |

`kubernetes.example.yaml` is a non-functional template showing probes and env wiring; adapt it to your platform.
