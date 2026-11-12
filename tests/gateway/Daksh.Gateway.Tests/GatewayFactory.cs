using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Hosts the real Gateway in-process (JWT auth, SignalR hub, all REST modules, every hosted background
/// service: RuntimeWorker, JobRunnerWorker, ChannelOutboxWorker) against a real PostgreSQL 17 + pgvector
/// container, matching docker-compose.yml's image exactly (the platform's migration runs raw SQL for
/// "CREATE EXTENSION vector", which a plain postgres image does not support).
///
/// This is the automated equivalent of running `tests/manual/e2e_scenario.py` /
/// `tests/manual/e2e_restart_recovery.py` against a hand-started Gateway: same HTTP surface, same scripted
/// provider/web-search backend, same background workers actually running on their normal timers - just
/// hosted in-process by WebApplicationFactory instead of a separately started process, and against a
/// disposable per-test-run container instead of the shared dev database.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("daksh_platform")
        .WithUsername("daksh")
        .WithPassword("change-me")
        .Build();

    Task IAsyncLifetime.InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = _postgres.GetConnectionString(),
                ["Database:MigrateOnStartup"] = "true",
                ["Tools:WebSearch:Provider"] = "scripted",
                // Speeds up the restart-recovery test without weakening what it proves: the catch-up query
                // (`RunAfter <= now`) matches a job regardless of how overdue it is, so a short poll interval
                // just means the test doesn't have to wait as long for that real background poll to happen.
                ["Scheduler:PollIntervalSeconds"] = "1",
            });
        });
    }
}

/// <summary>xUnit collection so every test in it shares one Gateway + container instead of starting a new
/// Postgres container per test class (each container start takes real seconds).</summary>
[CollectionDefinition(nameof(GatewayCollection))]
public sealed class GatewayCollection : ICollectionFixture<GatewayFactory>;
