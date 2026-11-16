#!/usr/bin/env bash
# Run the API on the host. MongoDB connection/credentials live in appsettings.Development.json
# (a remote Atlas cluster -- there is no local database container to start).
set -euo pipefail
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080
dotnet run --project services/api/Daksh.Api --no-launch-profile
