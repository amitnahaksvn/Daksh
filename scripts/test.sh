#!/usr/bin/env bash
# Run all test suites available on this machine. Set DAKSH_TEST_POSTGRES to also run the real-PostgreSQL migration test.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test Daksh.sln
(cd apps/web && npm ci && npm run lint && npm run typecheck && npm test && npm run build)
if command -v flutter >/dev/null; then (cd apps/mobile && flutter pub get && flutter analyze && flutter test); else echo "flutter not installed: skipping mobile"; fi
