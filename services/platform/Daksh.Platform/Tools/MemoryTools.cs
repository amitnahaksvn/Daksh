using System.Text.Json;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Tools.Core;

// Pending MongoDB rebuild: previously read agent memory config and queried PlatformDbContext.Agents.
public sealed class MemorySearchTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("memory_search", "Searches the agent's stored memories for relevant facts.", "memory",
        Schemas.Obj(new { query = Schemas.Str("What to recall."), limit = Schemas.Int("Maximum results (default 5, max 20).") }, ["query"]),
        ToolRisk.Low, TimeoutSeconds: 15, Idempotent: true, ReturnsUntrustedContent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Memory search is unavailable: pending MongoDB rebuild."));
}

public sealed class MemoryWriteTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("memory_write", "Saves a fact to the agent's long-term memory for later recall.", "memory",
        Schemas.Obj(new
        {
            content = Schemas.Str("The fact to remember, written clearly and self-contained."),
            kind = new { type = "string", @enum = new[] { "semantic", "episodic", "structured" } },
            importance = new { type = "number", minimum = 0, maximum = 1 },
        }, ["content"]), ToolRisk.Low, TimeoutSeconds: 15);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Memory write is unavailable: pending MongoDB rebuild."));
}
