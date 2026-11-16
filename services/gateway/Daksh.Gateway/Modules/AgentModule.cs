using Daksh.Gateway.Auth;
using Daksh.Platform.Identity;

namespace Daksh.Gateway.Modules;

public sealed record CreateAgentRequest(string Name, string? Description, string? Personality, string DefaultModel,
    List<string>? FallbackModels, string ToolProfile, string? SystemInstructions);
public sealed record AgentDto(Guid Id, string Name, string Slug, string Description, bool Enabled, string? DefaultModel, string ToolProfile);
public sealed record UpdateAgentConfigRequest(string? DefaultModel, List<string>? FallbackModels, string? ToolProfile,
    List<string>? ToolAllow, List<string>? ToolDeny, bool? MemoryEnabled, string? SandboxMode, string? WorkspaceAccess, string? ApprovalRiskThreshold);

// Pending MongoDB rebuild: previously persisted agents/configurations/instructions via PlatformDbContext.
public static class AgentModule
{
    public static void MapAgents(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/agents").RequireAuthorization().WithTags("Agents");
        var unavailable = () => Results.Problem(statusCode: 501, title: "Agents are unavailable: pending MongoDB rebuild.");

        g.MapPost("/", (CreateAgentRequest req) => unavailable()).RequirePermission(Permissions.AgentsWrite);
        g.MapGet("/", () => unavailable()).RequirePermission(Permissions.AgentsRead);
        g.MapGet("/{id:guid}", (Guid id) => unavailable()).RequirePermission(Permissions.AgentsRead);
        g.MapPatch("/{id:guid}/config", (Guid id, UpdateAgentConfigRequest req) => unavailable()).RequirePermission(Permissions.AgentsWrite);
        g.MapPost("/{id:guid}/disable", (Guid id) => unavailable()).RequirePermission(Permissions.AgentsWrite);
        g.MapDelete("/{id:guid}", (Guid id) => unavailable()).RequirePermission(Permissions.AgentsWrite);
    }
}
