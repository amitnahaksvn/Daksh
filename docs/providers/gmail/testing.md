# Testing

- Backend: `dotnet test tests/api/Daksh.Api.Tests --filter "FullyQualifiedName~Gmail"` — 108 tests, no network, no real inbox: scopes/profiles, config validation, classifier,
  deadline extraction, prompt-injection, rules engine (AND/OR/NOT, priority, stop), search builder (including operator-injection), attachment safety, reply policy, message parsing and threading headers, status mapping,
  follow-up planner, sync exclusions, backoff, quiet hours.
- Frontend: `npm run test` in `apps/web` — 25 tests including 11 for the control centre: loading real values, save/discard, version conflict, validation errors, account switching,
  permission profile + overrides, scope-upgrade prompt, destructive-rule restriction, disconnect confirmation.
- Other API tests (`AssistantTests`, `PlatformTests`) need a MongoDB at `localhost:27017` and time out without one; that is unrelated to Gmail.
- **Not covered by automated tests:** the HTTP endpoints against a mocked Gmail server, the sync engine/worker with mocked Gmail responses, OAuth callback, and the Pub/Sub endpoint with a real
  Mongo. The Pub/Sub token check (401 bad / 204 good) and clean startup were smoke-tested manually; authenticated flows have not been run against Google.
