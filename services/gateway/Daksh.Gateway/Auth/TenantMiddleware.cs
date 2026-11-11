using System.Security.Claims;
using Daksh.Platform.Data;
using Daksh.Platform.Identity;

namespace Daksh.Gateway.Auth;

/// <summary>
/// After JWT authentication runs, resolves the caller's membership in the workspace named by the
/// "X-Workspace-Id" header (or the workspace claim on the access token) and populates <see cref="TenantContext"/>
/// for the rest of the request pipeline. Every downstream query and write is scoped by this, never by trusting
/// a workspace id taken from the request body or route alone.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, WorkspaceService workspaces)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sub = context.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (sub is not null && Guid.TryParse(sub, out var userId))
            {
                tenant.SetUser(userId);
                tenant.Ip = context.Connection.RemoteIpAddress?.ToString();
                tenant.CorrelationId = context.TraceIdentifier;

                var header = context.Request.Headers["X-Workspace-Id"].FirstOrDefault();
                Guid? workspaceId = Guid.TryParse(header, out var fromHeader) ? fromHeader
                    : Guid.TryParse(context.User.FindFirstValue(TokenService.WorkspaceClaim), out var fromClaim) ? fromClaim : null;

                if (workspaceId is { } wid)
                {
                    try
                    {
                        var membership = await workspaces.ResolveMembershipAsync(wid, userId, context.RequestAborted);
                        tenant.SetWorkspace(wid, membership.Permissions, membership.Role.Rank);
                    }
                    catch (NotAMemberException) { /* left unscoped; endpoints that need a workspace reject with 403 below */ }
                }
            }
        }
        await next(context);
    }
}

/// <summary>Minimal-API endpoint filter requiring a permission in the resolved workspace, and that a workspace was resolved at all.</summary>
public sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (tenant.WorkspaceId is null)
            return Results.Problem(statusCode: 403, title: "No workspace was selected (send X-Workspace-Id) or you are not a member of it.");
        if (!tenant.Has(permission))
            return Results.Problem(statusCode: 403, title: $"Missing permission '{permission}'.");
        return await next(context);
    }
}

public static class PermissionEndpointExtensions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.AddEndpointFilter(new RequirePermissionFilter(permission));
}
