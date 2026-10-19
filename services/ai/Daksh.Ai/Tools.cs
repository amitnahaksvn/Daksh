namespace Daksh.Ai;

/// <summary>
/// An action the assistant may request. Tools with side effects (send email, create event, delete data) must set
/// <see cref="RequiresConfirmation"/>; the orchestrator never executes them without explicit user approval.
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    bool RequiresConfirmation { get; }
    Task<string> InvokeAsync(string userId, string jsonArguments, CancellationToken ct = default);
}

public sealed class AgentToolRegistry(IEnumerable<IAgentTool> tools)
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Name);
    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    /// <summary>Executes a tool call; side-effecting tools are refused unless the user confirmed.</summary>
    public async Task<string> InvokeAsync(string name, string userId, string args, bool userConfirmed, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var tool)) throw new KeyNotFoundException($"Unknown tool '{name}'.");
        if (tool.RequiresConfirmation && !userConfirmed)
            throw new InvalidOperationException($"Tool '{name}' requires explicit user confirmation.");
        return await tool.InvokeAsync(userId, args, ct);
    }
}
