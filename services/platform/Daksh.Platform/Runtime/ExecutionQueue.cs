using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Platform.Runtime;

/// <summary>
/// Creates executions and decides what to do with a message that arrives while one is already running, per the
/// agent's queue mode (RT-012). The actual run loop is driven by <see cref="AgentRuntimeEngine"/> via the worker.
/// </summary>
public sealed class ExecutionQueueService(PlatformDbContext db)
{
    public async Task<Execution> StartAsync(Session session, Agent agent, long inputSeq, string trigger, Guid? userId,
        Guid? parentExecutionId, int depth, string? modelOverride, string? idempotencyKey, string? delivery, CancellationToken ct)
    {
        if (idempotencyKey is not null)
        {
            var existing = await db.Executions.FirstOrDefaultAsync(e => e.WorkspaceId == session.WorkspaceId && e.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return existing;
        }
        Guid? rootId = null;
        if (parentExecutionId is { } pid)
            rootId = (await db.Executions.Where(e => e.Id == pid).Select(e => e.RootExecutionId).FirstOrDefaultAsync(ct));

        var execution = new Execution
        {
            WorkspaceId = session.WorkspaceId, SessionId = session.Id, AgentId = agent.Id, UserId = userId,
            ParentExecutionId = parentExecutionId, Depth = depth, Trigger = trigger, InputSeq = inputSeq,
            ModelOverride = modelOverride, IdempotencyKey = idempotencyKey, Delivery = delivery,
            Status = ExecutionStatus.Queued,
        };
        execution.RootExecutionId = rootId ?? execution.Id;
        db.Executions.Add(execution);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Unique index on (workspace, idempotencyKey): a concurrent caller created it first.
            return await db.Executions.FirstAsync(e => e.WorkspaceId == session.WorkspaceId && e.IdempotencyKey == idempotencyKey, ct);
        }
        return execution;
    }

    public Task<bool> HasActiveRunAsync(Guid sessionId, CancellationToken ct) =>
        db.Executions.AnyAsync(e => e.SessionId == sessionId &&
            (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.WaitingApproval), ct);

    public Task<int> QueuedCountAsync(Guid sessionId, CancellationToken ct) =>
        db.Executions.CountAsync(e => e.SessionId == sessionId && e.Status == ExecutionStatus.Queued, ct);

    public async Task RequestCancelAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await db.Executions.FindAsync([executionId], ct);
        if (execution is null) return;
        execution.CancelRequested = true;
        if (execution.Status == ExecutionStatus.Queued) execution.Status = ExecutionStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }
}
