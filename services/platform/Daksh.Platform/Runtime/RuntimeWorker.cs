using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Runtime;

/// <summary>
/// Pending MongoDB rebuild: previously claimed queued/lease-expired executions and ran them via
/// <see cref="AgentRuntimeEngine"/> against PlatformDbContext. Idles until that's rebuilt — there is
/// nothing to claim without a persistence layer.
/// </summary>
public sealed class RuntimeWorker(ILogger<RuntimeWorker> logger) : BackgroundService
{
    public static readonly string WorkerId = $"worker-{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Runtime worker {WorkerId} is idle: execution persistence is pending the MongoDB rebuild.", WorkerId);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}
