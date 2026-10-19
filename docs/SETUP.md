# Setup

## Prerequisites
.NET SDK 10 (`global.json` pins the feature band), Node.js 22+, Docker, Flutter stable (mobile). Copy `.env.example` to `.env`.

## Database
`docker compose up -d postgres` then see `infrastructure/database/README.md` for migrations. Connection string:
`ConnectionStrings__Daksh`.

## API
`./scripts/dev.sh` -> http://localhost:5080. Health: `/health/live`, `/health/ready`. OpenAPI: `/openapi/v1.json` (non-production).
Auth: `Auth__Mode=Development` (local only) or `Jwt` with `Auth__Authority`/`Auth__Audience`.
Clients may send `X-Dev-User: <name>` in Development to act as different users.

## Worker
`dotnet run --project services/worker/Daksh.Worker` (needs the same connection string; the API/migrations must have created the schema).

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
`./scripts/test.sh`, or `dotnet test Daksh.sln` (set `DAKSH_TEST_POSTGRES="Host=localhost;Database=daksh_test;Username=daksh;Password=change-me"`
to include the real-PostgreSQL migration test, which drops and recreates the named database).
`./scripts/validate.sh` checks for committed secrets and config validity.
