using System.Net;
using System.Net.Http.Json;
using Daksh.Domain;
using Daksh.Integrations;
using Daksh.TestSupport;
using Daksh.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daksh.Integration.Tests;

/// <summary>API creates a reminder -> durable job -> worker delivers a notification -> audit trail records both.</summary>
public class ReminderEndToEndTests : IClassFixture<DakshApiFactory>
{
    private readonly DakshApiFactory _f;
    public ReminderEndToEndTests(DakshApiFactory f) => _f = f;

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public async Task Reminder_created_via_api_is_delivered_by_worker()
    {
        var res = await _f.ClientFor("e2e").PostAsJsonAsync("/api/v1/reminders",
            new { text = "Call the dentist", remindAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var push = new MockPushNotificationSender();
        var processor = new JobProcessor(_f.Services.GetRequiredService<IServiceScopeFactory>(),
            [new ReminderDueHandler(push)], NullLogger<JobProcessor>.Instance,
            new Clock(DateTimeOffset.UtcNow.AddMinutes(2)), Options.Create(new WorkerOptions()));

        Assert.Equal(1, await processor.ProcessOnceAsync(default));

        var sent = Assert.Single(push.Sent);
        Assert.Equal("e2e", sent.UserId);
        Assert.Equal("Call the dentist", sent.Body);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DakshDbContext>();
        var actions = await db.AuditEvents.Where(a => a.UserId == "e2e").Select(a => a.Action).ToListAsync();
        Assert.Contains("reminder.create", actions);
        Assert.Contains("reminder.notified", actions);
    }
}

/// <summary>Runs only when DAKSH_TEST_POSTGRES is set (CI provides a postgres service); verifies migrations apply to real PostgreSQL.</summary>
public class PostgresMigrationTests
{
    [SkippableFact]
    public async Task Migrations_apply_and_jobs_enforce_unique_idempotency()
    {
        var cs = Environment.GetEnvironmentVariable("DAKSH_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(cs), "DAKSH_TEST_POSTGRES not set");
        var opts = new DbContextOptionsBuilder<DakshDbContext>().UseNpgsql(cs).Options;
        await using var db = new DakshDbContext(opts);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var key = Guid.NewGuid().ToString();
        db.Jobs.Add(new Job { Type = "t", IdempotencyKey = key });
        await db.SaveChangesAsync();
        await using var db2 = new DakshDbContext(opts);
        db2.Jobs.Add(new Job { Type = "t", IdempotencyKey = key });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }
}
