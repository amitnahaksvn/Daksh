# Setup

## Prerequisites
.NET SDK 10 (`global.json` pins the feature band), Node.js 22+, Docker, Flutter stable (mobile). Copy `.env.example` to `.env`.

## Database
MongoDB (a remote Atlas cluster) -- nothing to run locally. Set `MONGO_CONNECTION_STRING` and `MONGO_DATABASE`
in `.env`; `scripts/dev.sh` loads them into `Mongo__ConnectionString`/`Mongo__Database` for the API process.
See `docs/POSTGRES_LEGACY.md` for the retired PostgreSQL/EF Core setup this replaced.

## API
`./scripts/dev.sh` -> http://localhost:5080. Health: `/health/live`, `/health/ready`. OpenAPI: `/openapi/v1.json` (non-production).
Auth: `Auth__Mode=Development` (local only) or `Jwt` with `Auth__Authority`/`Auth__Audience`.
Clients may send `X-Dev-User: <name>` in Development to act as different users.

## Gmail
Gmail uses Google OAuth; Daksh never collects or stores a user's Google password. Enable the Gmail API in
Google Cloud Console, configure the OAuth consent screen, and create a Web application OAuth client with this
authorized redirect URI:

`http://localhost:5080/api/integrations/gmail/callback`

Store the OAuth client credentials in .NET user-secrets for local development:

```bash
dotnet user-secrets set "Gmail:OAuth:ClientId" "<client-id>" --project services/api/Daksh.Api
dotnet user-secrets set "Gmail:OAuth:ClientSecret" "<client-secret>" --project services/api/Daksh.Api
```

The default redirect URI is for local development. Set `Gmail:OAuth:RedirectUri` and
`Gmail:OAuth:FrontendUrl` for deployment, and provide the OAuth client ID and secret through the deployment's
secret configuration. The API also requires `Security:Encryption:Keys` to encrypt OAuth tokens at rest.
The requested Gmail scopes are read-only access and sending mail; Google may require OAuth app verification
before allowing accounts outside the configured test users to connect.

## Worker
`dotnet run --project services/worker/Daksh.Worker` (needs `Mongo__ConnectionString`/`Mongo__Database` set in its environment).

## Web
```bash
cd apps/web && cp .env.example .env.local && npm ci
npm run dev        # http://localhost:3000
npm run lint && npm run typecheck && npm test && npm run build
```

## Mobile
```bash
cd apps/mobile && flutter pub get
flutter analyze && flutter test
flutter run --dart-define=API_URL=http://10.0.2.2:5080 --dart-define=DEV_USER=dev-user   # Android emulator; iOS simulator: http://localhost:5080
```
iOS builds need macOS + Xcode. Flutter is not run in Docker.

## Tests
`./scripts/test.sh`, or `dotnet test Daksh.sln`.
`./scripts/validate.sh` checks for committed secrets and config validity.
