# API

Base path `/api/v1`. JSON. All endpoints require authentication and are scoped to the caller; another user's resources return 404.

- **Errors**: RFC 7807 problem details. Validation: `400` with `{ title, status, errors: { Field: [messages] }, traceId }`. Unhandled: `500` without internals.
- **Auth**: `Authorization: Bearer <OIDC JWT>`. Development mode only: `X-Dev-User`.
- **Health**: `GET /health/live`, `GET /health/ready` (checks PostgreSQL). **OpenAPI**: `GET /openapi/v1.json` outside Production.

| Method & path | Body | Result |
|---------------|------|--------|
| `POST /assistant/messages` | `{ message (1-4000), conversationId? }` | `200 { conversationId, messageId, reply, provider, isSimulated }`; `409` if privacy settings permit no model; audit event `assistant.message` |
| `GET /assistant/conversations` | | list `{ id, createdAt, messageCount }` |
| `GET /assistant/conversations/{id}/messages` | | messages, `404` if not yours |
| `GET /tasks` · `POST /tasks` | `{ title (1-300), dueAt? }` | `201` task · audit `task.create` |
| `POST /tasks/{id}/complete` | | task · audit `task.complete` |
| `GET /reminders` · `POST /reminders` | `{ text (1-500), remindAt (not past) }` | `201`; enqueues a worker job · audit `reminder.create` |
| `GET /privacy` · `PUT /privacy` | `{ memoryEnabled, allowProviderDataSharing, retentionDays (1-3650) }` | defaults all off · audit `privacy.update` |
| `GET /audit-events?take=50` | | caller's audit trail (max 200) |
| `GET /integrations` | | `{ key, category, provider, state, note }`; state `Mock` until a real adapter is verified |
