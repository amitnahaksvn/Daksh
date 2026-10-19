#!/usr/bin/env bash
# Start PostgreSQL in Docker, then run the API (and optionally the worker) on the host.
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose up -d postgres
export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080
dotnet run --project services/api/Daksh.Api --no-launch-profile
