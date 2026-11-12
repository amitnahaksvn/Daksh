using System.Text.Json;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Automation;

/// <summary>
/// Claims due <see cref="ScheduledJob"/> rows (PostgreSQL FOR UPDATE SKIP LOCKED, so multiple Gateway instances never
/// run the same job twice), runs the automation's action, reschedules recurring automations, and retries failures
/// with backoff before dead-lettering. Missed jobs (the Gateway was down when they were due) run once on the next poll.
/// </summary>
public sealed class JobRunnerWorker(IServiceScopeFactory scopes, IOptions<SchedulerOptions> options,
    IClock clock, ILogger<JobRunnerWorker> logger) : BackgroundService
{
    private static readonly string WorkerId = $"scheduler-{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduler poll failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenant.SetSystem();
        var now = clock.UtcNow;

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" FROM platform."ScheduledJobs"
            WHERE ("Status" = 'Pending' AND "RunAfter" <= {now}) OR ("Status" = 'Running' AND "LeaseUntil" < {now})
            ORDER BY "RunAfter" LIMIT {options.Value.BatchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        foreach (var id in ids)
        {
            using var jobScope = scopes.CreateScope();
            var jobDb = jobScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            jobDb.Tenant.SetSystem();
            var job = await jobDb.ScheduledJobs.FindAsync([id], ct);
            if (job is null) continue;
            job.Status = JobStatus.Running;
            job.LeaseUntil = now.AddSeconds(options.Value.LeaseSeconds);
            job.WorkerId = WorkerId;
            job.Attempts++;
            await jobDb.SaveChangesAsync(ct);
            await RunJobAsync(jobScope.ServiceProvider, job, ct);
        }
    }

    private async Task RunJobAsync(IServiceProvider services, ScheduledJob job, CancellationToken ct)
    {
        var db = services.GetRequiredService<PlatformDbContext>();
        var scheduler = services.GetRequiredService<SchedulerService>();
        db.Tenant.SetWorkspaceAsSystem(job.WorkspaceId);
        var automation = await db.Automations.FirstOrDefaultAsync(a => a.Id == job.AutomationId, ct);
        var jobExecution = new JobExecution { WorkspaceId = job.WorkspaceId, ScheduledJobId = job.Id, AutomationId = job.AutomationId, Attempt = job.Attempts };
        db.JobExecutions.Add(jobExecution);
        await db.SaveChangesAsync(ct);

        try
        {
            if (automation is null) throw new InvalidOperationException("Automation no longer exists.");
            if (automation.Status != "active") { job.Status = JobStatus.Cancelled; await db.SaveChangesAsync(ct); return; }

            var executionId = await RunActionAsync(services, automation, job, ct);
            jobExecution.Status = JobStatus.Succeeded;
            jobExecution.ExecutionId = executionId;
            jobExecution.FinishedAt = clock.UtcNow;
            job.Status = JobStatus.Succeeded;
            automation.LastRunAt = clock.UtcNow;
            automation.ConsecutiveFailures = 0;
            await db.SaveChangesAsync(ct);
            await scheduler.ScheduleNextAsync(automation, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automation {AutomationId} run {Attempt} failed", job.AutomationId, job.Attempts);
            jobExecution.Status = JobStatus.Failed;
            jobExecution.Error = ex.Message;
            jobExecution.FinishedAt = clock.UtcNow;
            if (automation is not null) automation.ConsecutiveFailures++;
            var retry = automation?.Retry ?? new AutomationRetry();
            if (job.Attempts < retry.MaxAttempts)
            {
                job.Status = JobStatus.Pending;
                job.RunAfter = clock.UtcNow.Add(ScheduleCalculator.RetryDelay(retry.BackoffSeconds, job.Attempts));
                job.LastError = ex.Message;
            }
            else
            {
                job.Status = JobStatus.Dead;
                job.LastError = ex.Message;
                if (automation is not null) await scheduler.ScheduleNextAsync(automation, ct);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<Guid> RunActionAsync(IServiceProvider services, Domain.Automation automation, ScheduledJob job, CancellationToken ct)
    {
        var db = services.GetRequiredService<PlatformDbContext>();
        if (automation.ActionKind != "agent_prompt")
            throw new NotSupportedException($"Automation action kind '{automation.ActionKind}' is not supported.");
        if (automation.AgentId is null) throw new InvalidOperationException("Automation has no agent to run.");

        var action = JsonDocument.Parse(automation.Action).RootElement;
        var prompt = action.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";

        Session session;
        if (automation.SessionId is { } sid && await db.Sessions.FirstOrDefaultAsync(s => s.Id == sid, ct) is { } existing)
            session = existing;
        else
        {
            session = new Session { WorkspaceId = automation.WorkspaceId, AgentId = automation.AgentId.Value, Kind = SessionKind.Automation, Title = $"Automation: {automation.Name}" };
            db.Sessions.Add(session);
            automation.SessionId = session.Id;
            await db.SaveChangesAsync(ct);
        }

        var agent = await db.Agents.FirstAsync(a => a.Id == automation.AgentId, ct);
        var message = new Message { WorkspaceId = session.WorkspaceId, SessionId = session.Id, Seq = session.NextSeq++, Role = "user", Content = prompt, Epoch = session.Epoch };
        db.Messages.Add(message);
        session.LastMessageAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var queue = services.GetRequiredService<ExecutionQueueService>();
        var execution = await queue.StartAsync(session, agent, message.Seq, "automation", automation.OwnerUserId, null, 0, null,
            $"automation:{automation.Id}:{job.ScheduledFor:O}", automation.Delivery, ct);

        // Claim the execution ourselves, the same optimistic way RuntimeWorker does, before running it: the row
        // is Queued and otherwise just as visible to RuntimeWorker's own poll loop as any chat-triggered one, so
        // without this both workers would run the same execution concurrently. Whichever of the two wins the
        // race to flip it to Running proceeds; the loser (rare - this claim happens immediately after creation)
        // waits for the winner to finish instead of running the engine a second time.
        execution.Status = ExecutionStatus.Running;
        execution.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(services.GetRequiredService<IOptions<RuntimeOptions>>().Value.LeaseSeconds);
        execution.WorkerId = $"scheduler-{Environment.MachineName}-{Environment.ProcessId}";
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            while (true)
            {
                await Task.Delay(500, ct);
                var current = await db.Executions.IgnoreQueryFilters().AsNoTracking().FirstAsync(e => e.Id == execution.Id, ct);
                if (current.Status is ExecutionStatus.Succeeded or ExecutionStatus.Failed or ExecutionStatus.Cancelled)
                    return execution.Id;
            }
        }

        var engine = services.GetRequiredService<AgentRuntimeEngine>();
        await engine.RunAsync(execution.Id, ct);
        return execution.Id;
    }
}
