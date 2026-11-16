using Daksh.Platform.Domain;

namespace Daksh.Platform.Automation;

/// <summary>
/// Pending MongoDB rebuild: previously computed and persisted the next occurrence of an automation as a
/// <see cref="ScheduledJob"/> row against PlatformDbContext. Every member throws until the Mongo-backed
/// implementation is written.
/// </summary>
public sealed class SchedulerService
{
    private static NotImplementedException Pending() => new("SchedulerService is unavailable: pending MongoDB rebuild.");

    public Task<ScheduledJob?> ScheduleNextAsync(Domain.Automation automation, CancellationToken ct) => throw Pending();

    /// <summary>Called by the webhook endpoint to enqueue an immediate run with the delivered payload.</summary>
    public Task<ScheduledJob> EnqueueNowAsync(Guid workspaceId, Guid automationId, string? payload, CancellationToken ct) => throw Pending();
}
