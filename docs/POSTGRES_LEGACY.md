# PostgreSQL legacy setup (archived)

> **This document is an archive only.** As of this change, the codebase no longer uses PostgreSQL,
> EF Core, or Npgsql anywhere. The project now connects to MongoDB (see `README.md` / `.env.example`
> for `Mongo__ConnectionString` / `Mongo__Database`). Nothing on this page reflects live code — it's
> kept so the old schema/config can be referenced or reconstructed later if ever needed.

## Why this exists

The project originally used two separate PostgreSQL databases via EF Core:

1. **`daksh`** — the "legacy personal-assistant" stack (`services/api`, `services/worker`,
   `services/shared/Daksh.Domain`), connection string name `Daksh`.
2. **`daksh_platform`** — the agent-platform stack (`services/platform/Daksh.Platform`,
   `services/gateway/Daksh.Gateway`), connection string name `Platform`.

Both were removed in favor of MongoDB. This doc captures what existed before removal.

---

## 1. DbContext classes

### `services/shared/Daksh.Domain/DakshDbContext.cs` (namespace `Daksh.Domain`)

```csharp
DbSet<Conversation> Conversations
DbSet<ConversationMessage> Messages
DbSet<TaskItem> Tasks
DbSet<Reminder> Reminders
DbSet<AuditEvent> AuditEvents
DbSet<PrivacySettings> PrivacySettings
DbSet<Job> Jobs
```

Used by `services/api` and `services/worker` against the `daksh` database.

### `services/platform/Daksh.Platform/Data/PlatformDbContext.cs` (namespace `Daksh.Platform.Data`)

Default schema `"platform"`. Multi-tenant: filtered every `IWorkspaceScoped` entity by `TenantContext`,
plus a `Guard()` override on `SaveChanges`/`SaveChangesAsync` enforcing tenant isolation at write time.

DbSets (50+):

```
Users, RefreshTokens, PasswordResetTokens, ApiKeys, Roles, Permissions, RolePermissions,
Workspaces, WorkspaceMembers, WorkspaceInvitations, AuditLogs, SystemSettings, IdempotencyRecords,
Agents, AgentConfigurations, AgentInstructions, AgentDelegations, ModelProviders, ProviderCredentials,
Models, Sessions, SessionParticipants, Messages, MessageAttachments, Executions, ExecutionEvents,
ExecutionCheckpoints, ToolDefinitions, ToolExecutions, ToolPermissions, Approvals, UsageRecords,
CostRecords, Files (StoredFile), Memories, MemoryEmbeddings, MemorySources, Skills, SkillVersions,
Plugins, PluginConfigurations, PluginLogs, Channels, ChannelAccounts, ChannelConnections,
ChannelMessages, ChannelRoutes, ChannelIdentities, Automations, ScheduledJobs, JobExecutions,
Webhooks, WebhookDeliveries, Devices, DevicePairings, DeviceCapabilities, DeviceEvents
```

Used Postgres-specific features heavily:
- `jsonb` column type via custom `JsonValueConverter<T>`/`JsonValueComparer<T>`
- `HasFilter("\"DeletedAt\" IS NULL")` partial-index syntax (Postgres quoted-identifier filters)
- `IsRowVersion()` concurrency tokens
- The `pgvector` extension (`CREATE EXTENSION vector`) for the planned memory/embeddings adapter —
  this is why the dev image was `pgvector/pgvector:pg17`, not plain `postgres`.

Used by `services/gateway` against the `daksh_platform` database.

---

## 2. NuGet packages (removed)

| Project | Packages |
|---|---|
| `services/shared/Daksh.Domain` | `Microsoft.EntityFrameworkCore 10.0.13`, `Microsoft.EntityFrameworkCore.Relational 10.0.13`, `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `Microsoft.EntityFrameworkCore.Design 10.0.13` |
| `services/platform/Daksh.Platform` | `Microsoft.EntityFrameworkCore 10.0.13`, `Microsoft.EntityFrameworkCore.Relational 10.0.13`, `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `Microsoft.EntityFrameworkCore.Design 10.0.13` |
| `services/api/Daksh.Api` | `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.13` |
| `services/gateway/Daksh.Gateway` | `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.13` |
| `tests/integration/Daksh.Integration.Tests` | `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`, `Microsoft.EntityFrameworkCore.InMemory 10.0.13` |
| `tests/api/Daksh.Api.Tests` | `Microsoft.EntityFrameworkCore.InMemory 10.0.13` |
| `tests/worker/Daksh.Worker.Tests` | `Microsoft.EntityFrameworkCore.InMemory 10.0.13` |
| `tests/platform/Daksh.Platform.Tests` | `Testcontainers.PostgreSql 4.16.0` |
| `tests/gateway/Daksh.Gateway.Tests` | `Testcontainers.PostgreSql 4.16.0` |

---

## 3. Migrations (deleted)

`services/shared/Daksh.Domain/Migrations/` — 1 migration:
- `20261019091542_InitialCreate.cs` / `.Designer.cs`
- `DakshDbContextModelSnapshot.cs`

`services/platform/Daksh.Platform/Data/Migrations/` — 2 migrations:
- `20261111164111_InitialCreate.cs` / `.Designer.cs`
- `20261112071622_ExecutionVersionConcurrencyToken.cs` / `.Designer.cs`
- `PlatformDbContextModelSnapshot.cs`

Design-time factories (used by `dotnet ef`):

```csharp
// services/shared/Daksh.Domain/DesignTimeFactory.cs
// IDesignTimeDbContextFactory<DakshDbContext>, read ConnectionStrings__Daksh env var, .UseNpgsql(cs)

// services/platform/Daksh.Platform/Data/DesignTimeFactory.cs
// IDesignTimeDbContextFactory<PlatformDbContext>, read ConnectionStrings__Platform env var,
// .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.Schema))
```

---

## 4. Old appsettings connection strings

```json
// services/api/Daksh.Api/appsettings.json
"ConnectionStrings": { "Daksh": "" }

// services/api/Daksh.Api/appsettings.Development.json
"ConnectionStrings": { "Daksh": "Host=localhost;Port=5432;Database=daksh;Username=daksh;Password=change-me" }

// services/worker/Daksh.Worker/appsettings.json
"ConnectionStrings": { "Daksh": "Host=localhost;Port=5432;Database=daksh;Username=daksh;Password=change-me" }

// services/gateway/Daksh.Gateway/appsettings.json
"ConnectionStrings": { "Platform": "" }

// services/gateway/Daksh.Gateway/appsettings.Development.json
"ConnectionStrings": { "Platform": "Host=localhost;Port=5432;Database=daksh_platform;Username=daksh;Password=change-me" }
```

---

## 5. Old DI / Program.cs wiring

```csharp
// services/api/Daksh.Api/Program.cs
builder.Services.AddDbContext<DakshDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Daksh")));
builder.Services.AddHealthChecks().AddDbContextCheck<DakshDbContext>("postgres", tags: ["ready"]);
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup") && app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DakshDbContext>().Database.Migrate();
}

// services/worker/Daksh.Worker/Program.cs
builder.Services.AddDbContext<DakshDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Daksh")));

// services/platform/Daksh.Platform/ServiceCollectionExtensions.cs
services.AddDbContext<PlatformDbContext>((sp, o) =>
    DesignTimeFactory.Configure(o, config.GetConnectionString("Platform")
        ?? throw new InvalidOperationException("ConnectionStrings:Platform is required.")));

// services/gateway/Daksh.Gateway/Program.cs
builder.Services.AddHealthChecks().AddDbContextCheck<PlatformDbContext>("postgres", tags: ["ready"]);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    db.Tenant.SetSystem();
    if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) await db.Database.MigrateAsync();
    var existingPermissions = await db.Permissions.Select(p => p.Key).ToListAsync();
    foreach (var key in Daksh.Platform.Identity.Permissions.All.Keys.Except(existingPermissions))
        db.Permissions.Add(new Daksh.Platform.Domain.Permission { Key = key, Description = Daksh.Platform.Identity.Permissions.All[key] });
    await db.SaveChangesAsync();
    await scope.ServiceProvider.GetRequiredService<ToolRegistry>().SyncDefinitionsAsync(db, scope.ServiceProvider, CancellationToken.None);
}
```

---

## 6. Old docker-compose.yml (full)

```yaml
services:
  postgres:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-daksh}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-change-me}
      POSTGRES_DB: ${POSTGRES_DB:-daksh}
    ports: ["${POSTGRES_PORT:-5432}:5432"]
    volumes: ["pgdata:/var/lib/postgresql/data"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-daksh}"]
      interval: 5s
      retries: 10

  api:
    build: { context: ., dockerfile: infrastructure/docker/api.Dockerfile }
    depends_on: { postgres: { condition: service_healthy } }
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Daksh: Host=postgres;Database=${POSTGRES_DB:-daksh};Username=${POSTGRES_USER:-daksh};Password=${POSTGRES_PASSWORD:-change-me}
      Auth__Mode: Development
    ports: ["5080:8080"]

  worker:
    build: { context: ., dockerfile: infrastructure/docker/worker.Dockerfile }
    depends_on: { postgres: { condition: service_healthy } }
    environment:
      ConnectionStrings__Daksh: Host=postgres;Database=${POSTGRES_DB:-daksh};Username=${POSTGRES_USER:-daksh};Password=${POSTGRES_PASSWORD:-change-me}

volumes:
  pgdata:
```

---

## 7. Old `.env` / `.env.example` entries

```
# --- PostgreSQL (docker-compose) ---
POSTGRES_USER=daksh
POSTGRES_PASSWORD=change-me
POSTGRES_DB=daksh
POSTGRES_PORT=5432

# --- API / Worker (legacy personal-assistant services) ---
ConnectionStrings__Daksh=Host=localhost;Port=5432;Database=daksh;Username=daksh;Password=change-me

# --- Agent platform (Daksh.Gateway) ---
ConnectionStrings__Platform=Host=localhost;Port=5432;Database=daksh_platform;Username=daksh;Password=change-me
```

---

## 8. Test infrastructure (removed)

- `tests/gateway/Daksh.Gateway.Tests/GatewayFactory.cs` — spun up a `Testcontainers.PostgreSql`
  container (`pgvector/pgvector:pg17` image) to host a real Gateway for integration tests; the
  migration ran raw `CREATE EXTENSION vector` SQL, so a plain `postgres` image wouldn't have worked.
- `tests/platform/Daksh.Platform.Tests/Runtime/ApprovalPauseResumeFixture.cs` — same pattern against
  a `daksh_platform_approval_test` database.
- `tests/TestSupport/DakshApiFactory.cs` — swapped `DakshDbContext` to EF Core's
  `UseInMemoryDatabase` for API tests.
- `tests/integration/Daksh.Integration.Tests/ReminderEndToEndTests.cs` — a real-Postgres migration
  test gated by the `DAKSH_TEST_POSTGRES` env var.
- `.github/workflows/ci.yml` — ran a `postgres:17` CI service container and set `DAKSH_TEST_POSTGRES`
  for the `dotnet` job.

---

## 9. What's stubbed, pending Mongo rebuild

Removing `PlatformDbContext` broke a large amount of business logic across `services/platform` and
`services/gateway` that was written directly against EF Core LINQ queries (Identity, Memory, Runtime,
Channels, Automation, Files, Tools, etc. — ~25 files). These were stubbed out (not reimplemented)
so the solution compiles; see the "pending Mongo work" note in the migration's final report/PR
description for the current list of stubbed call sites. None of that data access logic currently
works end-to-end — it needs to be rebuilt against MongoDB collections as they're designed, on
future tasks, per the project owner's direction ("when I ask, new tasks should use MongoDB").
