using Daksh.Gateway.Auth;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Daksh.Platform.Providers;
using Daksh.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record CreateWorkspaceRequest(string Name);
public sealed record WorkspaceDto(Guid Id, string Name, string Slug);
public sealed record CreateProviderRequest(string Name, string Kind, string? BaseUrl, string? ApiKey, Dictionary<string, string>? Headers, Dictionary<string, string>? Options);
public sealed record ProviderDto(Guid Id, string Name, string Kind, bool Enabled, string HealthStatus);
public sealed record CreateModelRequest(string ModelId, string? DisplayName, int ContextWindow, int MaxOutputTokens,
    bool Tools, bool Vision, bool StructuredOutput, bool Embeddings, decimal InputPricePerMTok, decimal OutputPricePerMTok);
public sealed record ModelDto(Guid Id, string ModelId, string DisplayName, ModelCapabilities Capabilities, bool Enabled);

public static class WorkspaceAndProviderModule
{
    public static void MapWorkspacesAndProviders(this IEndpointRouteBuilder app)
    {
        var w = app.MapGroup("/api/v1/workspaces").RequireAuthorization().WithTags("Workspaces");

        w.MapPost("/", async (CreateWorkspaceRequest req, WorkspaceService workspaces, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            var workspace = await workspaces.CreateAsync(req.Name, userId, ct);
            return Results.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.Slug));
        });

        w.MapGet("/", async (WorkspaceService workspaces, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            var memberships = await workspaces.ListMembershipsAsync(userId, ct);
            var ids = memberships.Select(m => m.WorkspaceId).ToList();
            var items = await db.Workspaces.IgnoreQueryFilters().Where(ws => ids.Contains(ws.Id))
                .Select(ws => new WorkspaceDto(ws.Id, ws.Name, ws.Slug)).ToListAsync(ct);
            return Results.Ok(items);
        });

        var p = app.MapGroup("/api/v1/providers").RequireAuthorization().WithTags("Providers");

        p.MapPost("/", async (CreateProviderRequest req, PlatformDbContext db, ProviderRegistry registry, ISecretProtector secrets, CancellationToken ct) =>
        {
            if (!registry.Supports(req.Kind)) return Results.Problem(statusCode: 400, title: $"Unknown provider kind '{req.Kind}'. Known: {string.Join(", ", registry.Kinds)}");
            var provider = new ModelProvider { Name = req.Name, Kind = req.Kind, BaseUrl = req.BaseUrl, Headers = req.Headers ?? [], Options = req.Options ?? [] };
            db.ModelProviders.Add(provider);
            if (!string.IsNullOrEmpty(req.ApiKey))
                db.ProviderCredentials.Add(new ProviderCredential { ProviderId = provider.Id, SecretEnc = secrets.Protect(req.ApiKey) });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ProviderDto(provider.Id, provider.Name, provider.Kind, provider.Enabled, provider.HealthStatus));
        }).RequirePermission(Permissions.ProvidersManage);

        p.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ModelProviders.Select(x => new ProviderDto(x.Id, x.Name, x.Kind, x.Enabled, x.HealthStatus)).ToListAsync(ct)))
            .RequirePermission(Permissions.ProvidersRead);

        p.MapPost("/{id:guid}/models", async (Guid id, CreateModelRequest req, PlatformDbContext db, CancellationToken ct) =>
        {
            var provider = await db.ModelProviders.FindAsync([id], ct);
            if (provider is null) return Results.NotFound();
            var model = new Model
            {
                ProviderId = id, ModelId = req.ModelId, DisplayName = req.DisplayName ?? req.ModelId,
                ContextWindow = req.ContextWindow, MaxOutputTokens = req.MaxOutputTokens,
                Capabilities = new ModelCapabilities { Tools = req.Tools, Vision = req.Vision, StructuredOutput = req.StructuredOutput, Embeddings = req.Embeddings },
                InputPricePerMTok = req.InputPricePerMTok, OutputPricePerMTok = req.OutputPricePerMTok,
            };
            db.Models.Add(model);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ModelDto(model.Id, model.ModelId, model.DisplayName, model.Capabilities, model.Enabled));
        }).RequirePermission(Permissions.ProvidersManage);

        p.MapGet("/{id:guid}/models", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Models.Where(m => m.ProviderId == id).Select(m => new ModelDto(m.Id, m.ModelId, m.DisplayName, m.Capabilities, m.Enabled)).ToListAsync(ct)))
            .RequirePermission(Permissions.ProvidersRead);

        p.MapPost("/{id:guid}/discover", async (Guid id, PlatformDbContext db, ProviderRegistry registry, ISecretProtector secrets, CancellationToken ct) =>
        {
            var provider = await db.ModelProviders.Include(x => x.Credentials).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (provider is null) return Results.NotFound();
            var endpoint = new ProviderEndpoint(provider.Kind, provider.BaseUrl,
                provider.Credentials.FirstOrDefault() is { } c ? secrets.Unprotect(c.SecretEnc) : null, provider.Headers, provider.Options);
            try
            {
                var discovered = await registry.Get(provider.Kind).DiscoverAsync(endpoint, ct);
                return Results.Ok(discovered);
            }
            catch (ProviderException ex) { return Results.Problem(statusCode: 502, title: ex.Message); }
        }).RequirePermission(Permissions.ProvidersManage);
    }

    internal static Guid CurrentUserId(HttpContext http)
    {
        var sub = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(sub, out var id) ? id : throw new InvalidOperationException("No authenticated user.");
    }
}
