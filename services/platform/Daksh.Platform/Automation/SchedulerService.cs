using Daksh.Platform.Automation;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Automation;

/// <summary>
/// Computes and persists the next occurrence of an automation as a <see cref="ScheduledJob"/> row. The unique
/// (automation, scheduledFor) index makes scheduling idempotent: calling this twice for the same slot is a no-op.
/// </summary>
public sealed class SchedulerService(PlatformDbContext db, ILogger<SchedulerService> logger)
{
    public async Task<ScheduledJob?> ScheduleNextAsync(Domain.Automation automation, CancellationToken ct)
    {
        if (automation.Status != "active") return null;
        var spec = new ScheduleSpec(automation.ScheduleKind ?? "at", automation.Cron, automation.TimeZone, automation.EverySeconds, automation.RunAt);
        var after = automation.LastRunAt ?? DateTimeOffset.UtcNow.AddSeconds(-1);
        var anchor = automation.CreatedAt;
        var next = ScheduleCalculator.Next(spec, after, anchor);
        if (next is null)
        {
            automation.Status = "completed";
            automation.NextRunAt = null;
            await db.SaveChangesAsync(ct);
            return null;
        }

        var existing = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.AutomationId == automation.Id && j.ScheduledFor == next.Value, ct);
        if (existing is not null) { automation.NextRunAt = next; await db.SaveChangesAsync(ct); return existing; }

        var job = new ScheduledJob
        {
            WorkspaceId = automation.WorkspaceId, AutomationId = automation.Id, ScheduledFor = next.Value, RunAfter = next.Value,
        };
        db.ScheduledJobs.Add(job);
        automation.NextRunAt = next;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Scheduled automation {AutomationId} for {Next}", automation.Id, next);
        return job;
    }

    /// <summary>Called by the webhook endpoint to enqueue an immediate run with the delivered payload.</summary>
    public async Task<ScheduledJob> EnqueueNowAsync(Guid workspaceId, Guid automationId, string? payload, CancellationToken ct)
    {
        var job = new ScheduledJob { WorkspaceId = workspaceId, AutomationId = automationId, ScheduledFor = DateTimeOffset.UtcNow, RunAfter = DateTimeOffset.UtcNow, Payload = payload };
        db.ScheduledJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }
}
