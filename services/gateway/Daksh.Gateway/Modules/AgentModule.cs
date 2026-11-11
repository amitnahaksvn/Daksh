using Daksh.Gateway.Auth;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record CreateAgentRequest(string Name, string? Description, string? Personality, string DefaultModel,
    List<string>? FallbackModels, string ToolProfile, string? SystemInstructions);
public sealed record AgentDto(Guid Id, string Name, string Slug, string Description, bool Enabled, string? DefaultModel, string ToolProfile);
public sealed record UpdateAgentConfigRequest(string? DefaultModel, List<string>? FallbackModels, string? ToolProfile,
    List<string>? ToolAllow, List<string>? ToolDeny, bool? MemoryEnabled, string? SandboxMode, string? WorkspaceAccess, string? ApprovalRiskThreshold);

public static class AgentModule
{
    public static void MapAgents(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/agents").RequireAuthorization().WithTags("Agents");

        g.MapPost("/", async (CreateAgentRequest req, PlatformDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem(statusCode: 400, title: "Name is required.");
            var slug = Slugify(req.Name);
            var i = 1;
            var baseSlug = slug;
            while (await db.Agents.AnyAsync(a => a.Slug == slug, ct)) slug = $"{baseSlug}-{++i}";

            var agent = new Agent { Name = req.Name, Slug = slug, Description = req.Description ?? "", Personality = req.Personality ?? "" };
            db.Agents.Add(agent);
            var config = new AgentConfiguration
            {
                AgentId = agent.Id, DefaultModel = req.DefaultModel, FallbackModels = req.FallbackModels ?? [],
                ToolPolicy = new ToolPolicySpec { Profile = req.ToolProfile },
            };
            agent.Configuration = config;
            db.AgentConfigurations.Add(config);
            if (!string.IsNullOrWhiteSpace(req.SystemInstructions))
                db.AgentInstructions.Add(new AgentInstruction { AgentId = agent.Id, Name = "system", Kind = "system", Content = req.SystemInstructions, Order = 0 });
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(agent, config));
        }).RequirePermission(Permissions.AgentsWrite);

        g.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var agents = await db.Agents.Include(a => a.Configuration).ToListAsync(ct);
            return Results.Ok(agents.Select(a => ToDto(a, a.Configuration)));
        }).RequirePermission(Permissions.AgentsRead);

        g.MapGet("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.Include(a => a.Configuration).Include(a => a.Instructions).FirstOrDefaultAsync(a => a.Id == id, ct);
            return agent is null ? Results.NotFound() : Results.Ok(new { agent, agent.Configuration });
        }).RequirePermission(Permissions.AgentsRead);

        g.MapPatch("/{id:guid}/config", async (Guid id, UpdateAgentConfigRequest req, PlatformDbContext db, CancellationToken ct) =>
        {
            var config = await db.AgentConfigurations.FirstOrDefaultAsync(c => c.AgentId == id, ct);
            if (config is null) return Results.NotFound();
            if (req.DefaultModel is not null) config.DefaultModel = req.DefaultModel;
            if (req.FallbackModels is not null) config.FallbackModels = req.FallbackModels;
            if (req.ToolProfile is not null) config.ToolPolicy.Profile = req.ToolProfile;
            if (req.ToolAllow is not null) config.ToolPolicy.Allow = req.ToolAllow;
            if (req.ToolDeny is not null) config.ToolPolicy.Deny = req.ToolDeny;
            if (req.MemoryEnabled is { } me) config.Memory.Enabled = me;
            if (req.SandboxMode is not null) config.SandboxMode = req.SandboxMode;
            if (req.WorkspaceAccess is not null) config.WorkspaceAccess = req.WorkspaceAccess;
            if (req.ApprovalRiskThreshold is not null) config.Approval.RiskThreshold = req.ApprovalRiskThreshold;
            await db.SaveChangesAsync(ct);
            return Results.Ok(config);
        }).RequirePermission(Permissions.AgentsWrite);

        g.MapPost("/{id:guid}/disable", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.FindAsync([id], ct);
            if (agent is null) return Results.NotFound();
            agent.Enabled = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequirePermission(Permissions.AgentsWrite);

        g.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.FindAsync([id], ct);
            if (agent is null) return Results.NotFound();
            agent.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AgentsWrite);
    }

    private static AgentDto ToDto(Agent a, AgentConfiguration? c) => new(a.Id, a.Name, a.Slug, a.Description, a.Enabled, c?.DefaultModel, c?.ToolPolicy.Profile ?? "chat");

    private static string Slugify(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "agent" : slug.Length > 60 ? slug[..60] : slug;
    }
}
