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
| `GET /integrations` | | Legacy mock-adapter data `{ key, category, provider, state, note }`; live app connections use the separate routes below |

## Gmail

These endpoints use `/api/integrations/gmail` (outside `/api/v1`) and require the signed-in user's Daksh
session, except for Google's OAuth callback. Gmail OAuth credentials and token encryption must be configured
as described in [Setup](./SETUP.md#gmail). Google access and refresh tokens are stored encrypted and are never
returned to the client.

| Method & path | Body / query | Result |
|---------------|--------------|--------|
| `POST /api/integrations/gmail/connect` | | `{ authorizationUrl }`; redirect the user to Google |
| `GET /api/integrations/gmail/callback` | Google OAuth callback | Redirects to the Gmail app with a connection status |
| `GET /api/integrations/gmail/messages?q=in:inbox&pageToken=...` | optional Gmail search query and page token | `{ messages, nextPageToken }` (20 messages per page) |
| `GET /api/integrations/gmail/messages/{messageId}` | | Message headers and plain-text body |
| `POST /api/integrations/gmail/messages` | `{ to, subject, body }` | `{ messageId }`; sends through Gmail |
| `POST /api/integrations/gmail/disconnect` | | Revokes Google access and removes stored credentials |
