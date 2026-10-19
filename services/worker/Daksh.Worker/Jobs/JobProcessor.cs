using Daksh.Domain;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Worker.Jobs;

public interface IJobHandler
{
    string Type { get; }
    /// <summary>Delivery is at-least-once, so implementations must be idempotent.</summary>
    Task HandleAsync(Job job, DakshDbContext db, CancellationToken ct);
}

public sealed class WorkerOptions
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 10;
    public int LockSeconds { get; set; } = 120;
}

/// <summary>
/// Claims due jobs with optimistic concurrency, runs the matching handler, and records outcome.
/// Failures back off exponentially and dead-letter after MaxAttempts. Shutdown cancellation releases the
/// job without consuming an attempt.
/// </summary>
public sealed class JobProcessor(IServiceScopeFactory scopes, IEnumerable<IJobHandler> handlers,
    ILogger<JobProcessor> logger, TimeProvider clock, Microsoft.Extensions.Options.IOptions<WorkerOptions> options)
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Type);

    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt) * 5, 3600));

    /// <summary>Processes one batch of due jobs; returns the number executed.</summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DakshDbContext>();
        var now = clock.GetUtcNow();

        var candidates = await db.Jobs
            .Where(j => (j.Status == JobStatus.Pending && j.RunAt <= now)
                     || (j.Status == JobStatus.Running && j.LockedUntil != null && j.LockedUntil < now))
            .OrderBy(j => j.RunAt).Take(options.Value.BatchSize).ToListAsync(ct);

        var executed = 0;
        foreach (var job in candidates)
        {
            try
            {
                job.Status = JobStatus.Running;
                job.LockedUntil = now.AddSeconds(options.Value.LockSeconds);
                job.Attempts++;
                job.Version++;
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(job).State = EntityState.Detached; // another worker claimed it
                continue;
            }

            executed++;
            await RunAsync(db, job, ct);
        }
        return executed;
    }

    private async Task RunAsync(DakshDbContext db, Job job, CancellationToken ct)
    {
        try
        {
            if (!_handlers.TryGetValue(job.Type, out var handler))
                throw new InvalidOperationException($"No handler registered for job type '{job.Type}'.");
            await handler.HandleAsync(job, db, ct);
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = clock.GetUtcNow();
            job.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = JobStatus.Pending;
            job.Attempts--;
            logger.LogInformation("Job {JobId} released on shutdown", job.Id);
        }
        catch (Exception ex)
        {
            job.LastError = ex.Message;
            if (job.Attempts >= job.MaxAttempts)
            {
                job.Status = JobStatus.DeadLetter;
                logger.LogError(ex, "Job {JobId} ({Type}) dead-lettered after {Attempts} attempts", job.Id, job.Type, job.Attempts);
            }
            else
            {
                job.Status = JobStatus.Pending;
                job.RunAt = clock.GetUtcNow().Add(Backoff(job.Attempts));
                logger.LogWarning(ex, "Job {JobId} ({Type}) failed on attempt {Attempts}; retrying at {RunAt}", job.Id, job.Type, job.Attempts, job.RunAt);
            }
        }
        job.LockedUntil = null;
        job.Version++;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

public sealed class JobWorker(JobProcessor processor, Microsoft.Extensions.Options.IOptions<WorkerOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Daksh worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await processor.ProcessOnceAsync(stoppingToken);
                if (n > 0) continue; // drain without waiting
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Worker poll failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("Daksh worker stopped");
    }
}
