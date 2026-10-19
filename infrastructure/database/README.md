# Database

PostgreSQL 16+ (docker-compose uses `pgvector/pgvector:pg17`). The schema is owned by EF Core migrations in
`services/shared/Daksh.Domain/Migrations`.

```bash
docker compose up -d postgres
dotnet tool restore
# apply migrations
ConnectionStrings__Daksh="Host=localhost;Database=daksh;Username=daksh;Password=change-me" \
  dotnet tool run dotnet-ef database update --project services/shared/Daksh.Domain
# create a new migration after changing entities
dotnet tool run dotnet-ef migrations add <Name> --project services/shared/Daksh.Domain --output-dir Migrations
```

In Development the API applies migrations on startup (`Database:MigrateOnStartup`). In other environments run
migrations as an explicit deploy step.
