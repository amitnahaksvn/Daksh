# Deployment

- Build: `dotnet build services/api/Daksh.Api`, `npm run build` in `apps/web` (settings live in the Manage popup).
- Config keys: `Gmail:OAuth:*`, `Gmail:PubSub:TopicName`, `Gmail:PubSub:VerificationToken`, `Security:Encryption:*`, `Mongo:*`, `Cors:AllowedOrigins`, `NEXT_PUBLIC_API_URL`.
- Startup creates MongoDB indexes (`EnsureGmailIndexesAsync`, `EnsureGmailManagementIndexesAsync`). First deployment against an existing database needs no data migration; existing connections are adopted lazily
  (a `gmailAccounts` record is created on first use, scopes assumed read+send until the user approves more).
- The API must be reachable over public HTTPS for the OAuth callback and Pub/Sub push. The sync worker runs in the API process.
- The WhatsApp/Telegram services are independent of Gmail.
- Health: existing `/health/live` and `/health/ready`. Sync health is per account in the control centre (status, last success/failure, dead letters).
