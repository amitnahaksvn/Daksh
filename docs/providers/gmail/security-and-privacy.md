# Security and privacy

- **Tokens**: encrypted at rest; never sent to the browser; not logged. Disconnect revokes the refresh token at Google first and only then removes it.
- **Authorization**: all endpoints require a Daksha user; queries are filtered by `UserId`. `GmailGuard` enforces account-enabled → permission profile/override → OAuth scope on every
  Gmail action. UI state is not trusted. Profiles: Read only, Assisted (default), Autonomous; permanent delete and external-AI are never part of a profile.
- **Audit log**: every mutating action records user, account, message/thread reference, action, trigger (User/Rule/Automation/Sync), rule, approval, status, error, time; filterable and searchable.
- **Webhook**: shared-secret token compared in constant time, de-duplicated, TTL-expired. Rotate by changing `Gmail:PubSub:VerificationToken` and the subscription URL. (OIDC-signed push JWT verification is not implemented.)
- **Attachments**: type detected from bytes; mismatches and executables/scripts are blocked; file names are sanitised (no path traversal); downloads are forced
  `application/octet-stream` with `nosniff` and `CSP: sandbox`; nothing is executed or stored. **No malware scanner is integrated** — none exists in the repo.
- **Prompt injection**: see [ai-intelligence.md](ai-intelligence.md). Email text never leaves the server for AI.
- **Data**: message bodies and attachment bytes are not stored; subject/snippet storage is optional; retention is enforced by the worker; `GET /export` and `DELETE /data` serve access/deletion requests;
  disconnecting deletes the account's local data.
- **Limits**: error messages are generic (no Google bodies, no stack traces). No per-user API rate limiting was added beyond Gmail's own limits and the retry/backoff policy.
