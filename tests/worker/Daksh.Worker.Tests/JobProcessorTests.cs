using Daksh.Domain;
using Daksh.Integrations;
using Daksh.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daksh.Worker.Tests;

public class JobProcessorTests
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingHandler(Func<int, Task>? behavior = null) : IJobHandler
    {
        public int Calls;
        public string Type => "test.job";
        public Task HandleAsync(Job job, DakshDbContext db, CancellationToken ct) => behavior?.Invoke(++Calls) ?? Task.FromResult(++Calls);
    }

    private static (JobProcessor p, ServiceProvider sp, FakeClock clock) Build(IJobHandler handler)
    {
        var name = Guid.NewGuid().ToString();
        var clock = new FakeClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sp = new ServiceCollection()
            .AddDbContext<DakshDbContext>(o => o.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        var p = new JobProcessor(sp.GetRequiredService<IServiceScopeFactory>(), [handler],
            NullLogger<JobProcessor>.Instance, clock, Options.Create(new WorkerOptions()));
        return (p, sp, clock);
    }

    private static async Task<Job> Seed(ServiceProvider sp, DateTimeOffset runAt, string type = "test.job", int max = 3)
    {
        using var s = sp.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DakshDbContext>();
        var j = new Job { Type = type, IdempotencyKey = Guid.NewGuid().ToString(), RunAt = runAt, MaxAttempts = max };
        db.Jobs.Add(j);
        await db.SaveChangesAsync();
        return j;
    }

    private static async Task<Job> Reload(ServiceProvider sp, Guid id)
    {
        using var s = sp.CreateScope();
        return await s.ServiceProvider.GetRequiredService<DakshDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Due_job_succeeds_and_future_job_is_not_run()
    {
        var h = new CountingHandler();
        var (p, sp, clock) = Build(h);
        var due = await Seed(sp, clock.Now.AddMinutes(-1));
        var future = await Seed(sp, clock.Now.AddHours(1));

        Assert.Equal(1, await p.ProcessOnceAsync(default));

        Assert.Equal(JobStatus.Succeeded, (await Reload(sp, due.Id)).Status);
        Assert.Equal(JobStatus.Pending, (await Reload(sp, future.Id)).Status);
        Assert.Equal(0, await p.ProcessOnceAsync(default)); // already done; not re-run
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Failure_is_retried_with_backoff_then_dead_lettered()
    {
        var h = new CountingHandler(_ => throw new InvalidOperationException("boom"));
        var (p, sp, clock) = Build(h);
        var job = await Seed(sp, clock.Now, max: 2);

        await p.ProcessOnceAsync(default);
        var after1 = await Reload(sp, job.Id);
        Assert.Equal(JobStatus.Pending, after1.Status);
        Assert.Equal("boom", after1.LastError);
        Assert.Equal(clock.Now + JobProcessor.Backoff(1), after1.RunAt);

        Assert.Equal(0, await p.ProcessOnceAsync(default)); // still backing off
        clock.Now = after1.RunAt;
        await p.ProcessOnceAsync(default);
        Assert.Equal(JobStatus.DeadLetter, (await Reload(sp, job.Id)).Status);
    }

    [Fact]
    public async Task Cancellation_releases_job_without_consuming_an_attempt()
    {
        using var cts = new CancellationTokenSource();
        var h = new CountingHandler(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var (p, sp, clock) = Build(h);
        var job = await Seed(sp, clock.Now);

        await p.ProcessOnceAsync(cts.Token);

        var j = await Reload(sp, job.Id);
        Assert.Equal(JobStatus.Pending, j.Status);
        Assert.Equal(0, j.Attempts);
    }

    [Fact]
    public async Task Unknown_job_type_fails_visibly()
    {
        var (p, sp, clock) = Build(new CountingHandler());
        var job = await Seed(sp, clock.Now, type: "nope", max: 1);
        await p.ProcessOnceAsync(default);
        var j = await Reload(sp, job.Id);
        Assert.Equal(JobStatus.DeadLetter, j.Status);
        Assert.Contains("No handler", j.LastError);
    }

    [Fact]
    public async Task Idempotency_key_has_unique_index()
    {
        var (_, sp, clock) = Build(new CountingHandler());
        using var s = sp.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<DakshDbContext>();
        Assert.Contains(db.Model.FindEntityType(typeof(Job))!.GetIndexes(),
            i => i.IsUnique && i.Properties.Single().Name == nameof(Job.IdempotencyKey));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Reminder_handler_is_idempotent()
    {
        var push = new MockPushNotificationSender();
        var handler = new ReminderDueHandler(push);
        var (p, sp, clock) = Build(handler);
        Guid rid;
        using (var s = sp.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<DakshDbContext>();
            var r = new Reminder { UserId = "u1", Text = "stand up", RemindAt = clock.Now };
            rid = r.Id;
            db.Reminders.Add(r);
            await db.SaveChangesAsync();
        }
        using var s2 = sp.CreateScope();
        var ctx = s2.ServiceProvider.GetRequiredService<DakshDbContext>();
        var job = new Job { Type = JobTypes.ReminderDue, Payload = $"{{\"reminderId\":\"{rid}\"}}" };
        await handler.HandleAsync(job, ctx, default);
        await handler.HandleAsync(job, ctx, default);
        Assert.Single(push.Sent);
    }
}
