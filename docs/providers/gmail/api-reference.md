# API reference

Base `/api/integrations/gmail`, user-authenticated unless noted. Errors are RFC 7807 problem details with an optional `code`
(`scope_upgrade_required`, `permission_denied`, `confirmation_required`, `version_conflict`, `account_disabled`, `not_connected`,
`gmail_reauth_required`, `feature_disabled`). Gmail's own 401 is returned as **409** `gmail_reauth_required` so the web app doesn't treat it as a Daksha session expiry.
Most endpoints take `?accountId=` (default: default enabled account).

**Accounts** `POST /connect?features=` · `GET /callback` (anonymous) · `GET /accounts` · `PATCH /accounts/{id}` (nickname, enabled, isDefault) ·
`POST /accounts/{id}/test` · `GET /accounts/{id}/permissions` · `POST /disconnect?accountId=` (reconnect = connect again).
**Config** `GET|PUT /config`.
**Sync** `POST /sync/run` · `POST /sync/start|stop` · `GET /sync/status` · `GET /sync/history` · `POST /sync/watch/renew` · `POST /push?token=` (anonymous, Pub/Sub).
**Messages** `GET /messages?q=&pageToken=` · `GET /messages/{id}` · `POST /messages` (send) · `POST /messages/{id}/actions/{archive|unarchive|read|unread|star|unstar|important|unimportant|trash|untrash}` ·
`POST /messages/{id}/labels` · `POST /messages/{id}/reply` (draft or send, threaded) · `POST /messages/{id}/forward` (text only) ·
`DELETE /messages/{id}?confirm=true` (permanent; needs the `permanentDelete` override and the full scope) · `GET /messages/{id}/attachments` · `GET /messages/{id}/attachments/{attachmentId}`.
**Threads** `GET /threads` · `GET /threads/{id}` · `POST /threads/{id}/summarise` · `POST /threads/{id}/reply-draft`.
**Drafts** `GET /drafts` · `GET|PUT|DELETE /drafts/{id}` · `POST /drafts` · `POST /drafts/{id}/send`.
**Labels/filters** `GET|POST /labels` · `PUT|DELETE /labels/{id}` · `GET|POST /filters` · `DELETE /filters/{id}` · `POST /filters/test`.
**Gmail settings** `GET /account-settings` · `PUT /account-settings/vacation|language` · `PATCH /account-settings/send-as/{email}`.
**Search** `POST /search/build` · `GET|POST /saved-searches` · `DELETE /saved-searches/{id}`.
**AI** `POST /messages/{id}/analyze` (classification, summary, action items, deadlines, reply decision + suggestion) · `POST /messages/{id}/classification/correct` · `GET /analysis`.
**Rules** `GET|POST /rules` · `PUT|DELETE /rules/{id}` · `POST /rules/{id}/duplicate|enabled` · `POST /rules/test` · `POST /rules/{id}/preview|run` · `GET /rules/{id}/runs`.
**Follow-ups** `GET /follow-ups` · `PATCH /follow-ups/{threadId}` · `POST /follow-ups/{threadId}/draft`.
**Notifications / audit** `GET /notifications` · `POST /notifications/read` · `GET /audit?action=&status=&trigger=&q=&skip=&take=`.
**Data** `GET /export` · `DELETE /data?confirm=true&includeAudit=`. **Other** `GET /action-items`.

Pagination: Gmail page tokens for message/thread lists; `skip/take` for audit. Validation errors use `400` with field errors.
