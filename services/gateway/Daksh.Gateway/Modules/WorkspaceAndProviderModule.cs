using Daksh.Gateway.Auth;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;

namespace Daksh.Gateway.Modules;

public sealed record CreateWorkspaceRequest(string Name);
public sealed record WorkspaceDto(Guid Id, string Name, string Slug);
public sealed record CreateProviderRequest(string Name, string Kind, string? BaseUrl, string? ApiKey, Dictionary<string, string>? Headers, Dictionary<string, string>? Options);
public sealed record ProviderDto(Guid Id, string Name, string Kind, bool Enabled, string HealthStatus);
public sealed record CreateModelRequest(string ModelId, string? DisplayName, int ContextWindow, int MaxOutputTokens,
    bool Tools, bool Vision, bool StructuredOutput, bool Embeddings, decimal InputPricePerMTok, decimal OutputPricePerMTok);
public sealed record ModelDto(Guid Id, string ModelId, string DisplayName, ModelCapabilities Capabilities, bool Enabled);

// Pending MongoDB rebuild: previously persisted workspaces/providers/models via PlatformDbContext.
public static class WorkspaceAndProviderModule
{
    public static void MapWorkspacesAndProviders(this IEndpointRouteBuilder app)
    {
        var unavailable = () => Results.Problem(statusCode: 501, title: "This feature is unavailable: pending MongoDB rebuild.");

        var w = app.MapGroup("/api/v1/workspaces").RequireAuthorization().WithTags("Workspaces");
        w.MapPost("/", (CreateWorkspaceRequest req, HttpContext http) => unavailable());
        w.MapGet("/", (HttpContext http) => unavailable());

        var p = app.MapGroup("/api/v1/providers").RequireAuthorization().WithTags("Providers");
        p.MapPost("/", (CreateProviderRequest req) => unavailable()).RequirePermission(Permissions.ProvidersManage);
        p.MapGet("/", () => unavailable()).RequirePermission(Permissions.ProvidersRead);
        p.MapPost("/{id:guid}/models", (Guid id, CreateModelRequest req) => unavailable()).RequirePermission(Permissions.ProvidersManage);
        p.MapGet("/{id:guid}/models", (Guid id) => unavailable()).RequirePermission(Permissions.ProvidersRead);
        p.MapPost("/{id:guid}/discover", (Guid id) => unavailable()).RequirePermission(Permissions.ProvidersManage);
    }

    internal static Guid CurrentUserId(HttpContext http)
    {
        var sub = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(sub, out var id) ? id : throw new InvalidOperationException("No authenticated user.");
    }
}
