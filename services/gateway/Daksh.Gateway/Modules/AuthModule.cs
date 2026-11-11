using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? WorkspaceName);
public sealed record LoginRequest(string Email, string Password, string? MfaCode);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, Guid? WorkspaceId);
public sealed record UserProfile(Guid Id, string Email, string DisplayName, bool MfaEnabled);

public static class AuthModule
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/auth").WithTags("Auth");

        g.MapPost("/register", async (RegisterRequest req, IdentityService identity, WorkspaceService workspaces, TokenService tokens,
            PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                return Results.Problem(statusCode: 400, title: "A valid email is required.");
            User user;
            try { user = await identity.RegisterAsync(req.Email, req.Password, req.DisplayName, ct); }
            catch (DuplicateEmailException ex) { return Results.Problem(statusCode: 409, title: ex.Message); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, title: ex.Message); }

            var workspace = await workspaces.CreateAsync(string.IsNullOrWhiteSpace(req.WorkspaceName) ? $"{req.DisplayName}'s workspace" : req.WorkspaceName, user.Id, ct);
            var membership = await workspaces.ResolveMembershipAsync(workspace.Id, user.Id, ct);
            var issued = await tokens.IssueAsync(user, workspace.Id, membership.Permissions, null, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);
            return Results.Ok(new TokenResponse(issued.AccessToken, issued.RefreshToken, issued.AccessExpiresAt, workspace.Id));
        });

        g.MapPost("/login", async (LoginRequest req, IdentityService identity, WorkspaceService workspaces, TokenService tokens, HttpContext http, CancellationToken ct) =>
        {
            LoginResult result;
            try { result = await identity.LoginAsync(req.Email, req.Password, req.MfaCode, ct); }
            catch (InvalidCredentialsException) { return Results.Problem(statusCode: 401, title: "Invalid email or password."); }
            catch (MfaRequiredException) { return Results.Problem(statusCode: 401, title: "A second-factor code is required.", type: "mfa_required"); }
            catch (AccountLockedException ex) { return Results.Problem(statusCode: 423, title: ex.Message); }

            var memberships = await workspaces.ListMembershipsAsync(result.User.Id, ct);
            var workspaceId = memberships.FirstOrDefault()?.WorkspaceId;
            IReadOnlySet<string> permissions = workspaceId is { } wid ? (await workspaces.ResolveMembershipAsync(wid, result.User.Id, ct)).Permissions : new HashSet<string>();
            var issued = await tokens.IssueAsync(result.User, workspaceId, permissions, null, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);
            return Results.Ok(new TokenResponse(issued.AccessToken, issued.RefreshToken, issued.AccessExpiresAt, workspaceId));
        });

        g.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, WorkspaceService workspaces, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var (user, rawRefresh, expiresAt) = await tokens.RotateAsync(req.RefreshToken, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, ct);
                var memberships = await workspaces.ListMembershipsAsync(user.Id, ct);
                var workspaceId = memberships.FirstOrDefault()?.WorkspaceId;
                IReadOnlySet<string> permissions = workspaceId is { } wid ? (await workspaces.ResolveMembershipAsync(wid, user.Id, ct)).Permissions : new HashSet<string>();
                var access = tokens.CreateAccessToken(user, workspaceId, permissions);
                return Results.Ok(new TokenResponse(access, rawRefresh, expiresAt, workspaceId));
            }
            catch (RefreshTokenReuseException ex) { return Results.Problem(statusCode: 401, title: ex.Message); }
            catch (Exception) { return Results.Problem(statusCode: 401, title: "Refresh token is invalid or expired."); }
        });

        g.MapGet("/me", (HttpContext http, PlatformDbContext db, CancellationToken ct) => GetMeAsync(http, db, ct)).RequireAuthorization();

        static async Task<IResult> GetMeAsync(HttpContext http, PlatformDbContext db, CancellationToken ct)
        {
            var sub = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(sub, out var id)) return Results.Unauthorized();
            var user = await db.Users.FindAsync([id], ct);
            return user is null ? Results.NotFound() : Results.Ok(new UserProfile(user.Id, user.Email, user.DisplayName, user.MfaEnabled));
        }
    }
}
