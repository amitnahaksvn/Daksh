using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Platform.Tools.Core;

public sealed class MemorySearchTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("memory_search", "Searches the agent's stored memories for relevant facts.", "memory",
        Schemas.Obj(new { query = Schemas.Str("What to recall."), limit = Schemas.Int("Maximum results (default 5, max 20).") }, ["query"]),
        ToolRisk.Low, TimeoutSeconds: 15, Idempotent: true, ReturnsUntrustedContent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var service = context.Services.GetRequiredService<MemoryService>();
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var agent = await db.Agents.Include(a => a.Configuration).FirstOrDefaultAsync(a => a.Id == context.AgentId, ct);
        var readScopes = agent?.Configuration?.Memory.ReadScopes ?? ["agent", "user", "workspace"];
        var limit = Math.Clamp(arguments.Int("limit") ?? 5, 1, 20);
        var hits = await service.SearchAsync(context.WorkspaceId, context.AgentId, context.UserId, readScopes, arguments.Required("query"), limit, 0.1, ct);
        if (hits.Count == 0) return ToolResult.Success("No memories found for this query.");
        return ToolResult.Success(Security.Untrusted.Wrap("memory",
            string.Join("\n", hits.Select(h => $"- ({h.Memory.Kind}, relevance {h.Score:0.00}) {h.Memory.Content}"))));
    }
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

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var service = context.Services.GetRequiredService<MemoryService>();
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var agent = await db.Agents.Include(a => a.Configuration).FirstOrDefaultAsync(a => a.Id == context.AgentId, ct);
        if (agent?.Configuration?.Memory.Enabled == false) return ToolResult.Fail("Memory is disabled for this agent.");
        var scope = agent?.Configuration?.Memory.WriteScope ?? "agent";
        var memory = await service.WriteAsync(context.WorkspaceId, new MemoryWriteRequest(
            arguments.Required("content"), scope, arguments.Str("kind") ?? "semantic",
            scope == "agent" ? context.AgentId : null, scope == "user" ? context.UserId : null, context.SessionId,
            Importance: arguments.Prop("importance")?.GetDouble() ?? 0.5,
            SourceType: "message", SourceId: context.ExecutionId.ToString()), ct);
        return ToolResult.Success($"Saved to memory (id {memory.Id}).");
    }
}
