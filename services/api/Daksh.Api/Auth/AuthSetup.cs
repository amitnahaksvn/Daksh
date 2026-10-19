using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Daksh.Api.Auth;

public static class AuthSetup
{
    public const string DevScheme = "DakshDev";

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

        services.AddAuthorization();
        return services;
    }

    public static string UserId(this ClaimsPrincipal p) =>
        p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub")
        ?? throw new InvalidOperationException("Authenticated principal has no subject claim.");
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
