using Daksh.Platform.Domain;

namespace Daksh.Platform.Runtime;

public sealed record RunOutcome(ExecutionStatus Status, string? FinalText, string? Error);

/// <summary>
/// Pending MongoDB rebuild: previously drove one execution from its current step to completion, interruption
/// (approval needed) or failure, persisting every message/tool call/token count to PlatformDbContext along the
/// way. The whole run loop is unavailable until the Mongo-backed implementation is written.
/// </summary>
public sealed class AgentRuntimeEngine
{
    public Task<RunOutcome> RunAsync(Guid executionId, CancellationToken ct) =>
        throw new NotImplementedException("AgentRuntimeEngine is unavailable: pending MongoDB rebuild.");

    public static string AgentWorkspacePath(Guid workspaceId, Guid agentId) =>
        Path.Combine(AppContext.BaseDirectory, "data", "workspaces", workspaceId.ToString("N"), agentId.ToString("N"));
}
