using Daksh.Domain;

namespace Daksh.Worker.Jobs;

public interface IJobHandler
{
    string Type { get; }
    /// <summary>Delivery is at-least-once, so implementations must be idempotent.</summary>
    Task HandleAsync(Job job, CancellationToken ct);
}

public sealed class WorkerOptions
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 10;
    public int LockSeconds { get; set; } = 120;
}

/// <summary>
/// Pending MongoDB rebuild: previously claimed due jobs with optimistic concurrency against DakshDbContext.Jobs,
/// ran the matching handler, and recorded outcome. Idles (<see cref="ProcessOnceAsync"/> always returns 0)
/// until that's rebuilt.
/// </summary>
public sealed class JobProcessor(IEnumerable<IJobHandler> handlers)
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Type);

    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt) * 5, 3600));

    /// <summary>Processes one batch of due jobs; returns the number executed.</summary>
    public Task<int> ProcessOnceAsync(CancellationToken ct) => Task.FromResult(0);
}

public sealed class JobWorker(JobProcessor processor, Microsoft.Extensions.Options.IOptions<WorkerOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Daksh worker started, but job persistence is pending the MongoDB rebuild (idling).");
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
