namespace Daksh.Gateway.Modules;

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string? WorkspaceName);
public sealed record LoginRequest(string Email, string Password, string? MfaCode);
public sealed record RefreshRequest(string RefreshToken);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, Guid? WorkspaceId);
public sealed record UserProfile(Guid Id, string Email, string DisplayName, bool MfaEnabled);

// Pending MongoDB rebuild: previously persisted users/workspaces/refresh tokens via PlatformDbContext.
public static class AuthModule
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/auth").WithTags("Auth");

        g.MapPost("/register", (RegisterRequest req) =>
            Results.Problem(statusCode: 501, title: "Registration is unavailable: pending MongoDB rebuild."));

        g.MapPost("/login", (LoginRequest req) =>
            Results.Problem(statusCode: 501, title: "Login is unavailable: pending MongoDB rebuild."));

        g.MapPost("/refresh", (RefreshRequest req) =>
            Results.Problem(statusCode: 501, title: "Token refresh is unavailable: pending MongoDB rebuild."));

        g.MapGet("/me", (HttpContext http) =>
            Results.Problem(statusCode: 501, title: "User profile is unavailable: pending MongoDB rebuild.")).RequireAuthorization();
    }
}
