using Daksh.Platform.Domain;

namespace Daksh.Platform.Runtime;

/// <summary>
/// Pending MongoDB rebuild: previously created executions and decided what to do with a message that arrives
/// while one is already running (RT-012) against PlatformDbContext. Every member throws until the Mongo-backed
/// implementation is written.
/// </summary>
public sealed class ExecutionQueueService
{
    private static NotImplementedException Pending() => new("ExecutionQueueService is unavailable: pending MongoDB rebuild.");

    public Task<Execution> StartAsync(Session session, Agent agent, long inputSeq, string trigger, Guid? userId,
        Guid? parentExecutionId, int depth, string? modelOverride, string? idempotencyKey, string? delivery, CancellationToken ct) => throw Pending();

    public Task<bool> HasActiveRunAsync(Guid sessionId, CancellationToken ct) => throw Pending();
    public Task<int> QueuedCountAsync(Guid sessionId, CancellationToken ct) => throw Pending();
    public Task RequestCancelAsync(Guid executionId, CancellationToken ct) => throw Pending();
}
