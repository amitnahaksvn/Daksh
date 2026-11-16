using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Automation;

/// <summary>
/// Pending MongoDB rebuild: previously claimed due <see cref="Domain.ScheduledJob"/> rows and ran each
/// automation's action against PlatformDbContext. Idles until that's rebuilt — there is nothing to claim
/// without a persistence layer.
/// </summary>
public sealed class JobRunnerWorker(ILogger<JobRunnerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Job runner worker is idle: automation persistence is pending the MongoDB rebuild.");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}
