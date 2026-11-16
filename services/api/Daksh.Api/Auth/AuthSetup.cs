using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Daksh.Api.Auth;

public static class AuthSetup
{
    public const string DevScheme = "DakshDev";

    /// <summary>
    /// Self-issued user session scheme (tokens minted by AccountModule's /login and /verify-otp). This is
    /// separate from, and always registered alongside, whatever Auth:Mode selects as the DEFAULT scheme
    /// (DakshDev's X-Dev-User header, or an OIDC authority) — endpoints that need a real per-user identity
    /// (not the shared "dev-user" fallback) require this scheme explicitly instead of relying on the default.
    /// </summary>
    public const string UserScheme = "DakshUser";

    /// <summary>
    /// Auth:Mode = "Jwt" validates bearer tokens from the configured OIDC Authority (the integration point
    /// for the identity provider). Auth:Mode = "Development" accepts any request as a fixed local user and
    /// is refused outside the Development/Testing environments.
    /// </summary>
    public static IServiceCollection AddDakshAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var mode = config["Auth:Mode"] ?? "Jwt";
        var builder = services.AddAuthentication(mode == "Development" ? DevScheme : JwtBearerDefaults.AuthenticationScheme);

        if (mode == "Development")
        {
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
                throw new InvalidOperationException("Auth:Mode=Development is only allowed in Development or Testing environments.");
            builder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, _ => { });
        }
        else
        {
            var authority = config["Auth:Authority"];
            if (string.IsNullOrWhiteSpace(authority))
                throw new InvalidOperationException("Auth:Authority must be configured when Auth:Mode=Jwt.");
            builder.AddJwtBearer(o =>
            {
                o.Authority = authority;
                o.Audience = config["Auth:Audience"];
            });
        }

        var signingKey = config["Auth:Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            builder.AddJwtBearer(UserScheme, o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = config["Auth:Jwt:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = config["Auth:Jwt:Audience"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
                    NameClaimType = "sub",
                };
                // SignalR's WebSocket/SSE transport can't set an Authorization header, so its JS client sends
                // the token as ?access_token=... instead (see apps/web/src/lib/signalr.ts). Without this, the
                // hub's negotiate call authenticates fine (it's a normal HTTP request) but the actual
                // long-lived transport connection that follows gets silently rejected.
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var accessToken = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            ctx.Token = accessToken;
                        return Task.CompletedTask;
                    },
                };
            });
        }

        services.AddAuthorization(o => o.AddPolicy(UserScheme, p => p.AddAuthenticationSchemes(UserScheme).RequireAuthenticatedUser()));
        return services;
    }

    public static string UserId(this ClaimsPrincipal p) =>
        p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub")
        ?? throw new InvalidOperationException("Authenticated principal has no subject claim.");
}

/// <summary>Mints the self-issued session tokens validated by the <see cref="AuthSetup.UserScheme"/> above.</summary>
public sealed class UserTokenService(IConfiguration config)
{
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(string userId, string email)
    {
        var signingKey = config["Auth:Jwt:SigningKey"] ?? throw new InvalidOperationException("Auth:Jwt:SigningKey is required.");
        var minutes = config.GetValue<int?>("Auth:Jwt:AccessTokenMinutes") ?? 60;
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: config["Auth:Jwt:Issuer"],
            audience: config["Auth:Jwt:Audience"],
            claims: [new Claim("sub", userId), new Claim(ClaimTypes.NameIdentifier, userId), new Claim("email", email)],
            expires: expires.UtcDateTime,
            signingCredentials: credentials);
        return (handler.WriteToken(token), expires);
    }
}

internal sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Allows tests/local clients to act as different users via X-Dev-User; defaults to "dev-user".
        var user = Request.Headers["X-Dev-User"].FirstOrDefault() is { Length: > 0 } h ? h : "dev-user";
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], AuthSetup.DevScheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DevScheme)));
    }
}
