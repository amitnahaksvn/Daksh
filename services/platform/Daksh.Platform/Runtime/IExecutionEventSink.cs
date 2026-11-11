namespace Daksh.Platform.Runtime;

/// <summary>Publishes live run events to whoever is watching (SignalR hub). Implemented in the Gateway; no-op by default.</summary>
public interface IExecutionEventSink
{
    Task PublishAsync(Guid workspaceId, Guid sessionId, Guid executionId, string type, object data, CancellationToken ct);
}

public sealed class NullExecutionEventSink : IExecutionEventSink
{
    public Task PublishAsync(Guid workspaceId, Guid sessionId, Guid executionId, string type, object data, CancellationToken ct) => Task.CompletedTask;
}
