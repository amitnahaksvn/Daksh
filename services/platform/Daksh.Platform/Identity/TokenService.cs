using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Daksh.Platform.Identity;

public sealed record IssuedTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt, DateTimeOffset RefreshExpiresAt);

public sealed class SecurityStampMismatchException(string message) : Exception(message);
public sealed class RefreshTokenReuseException(string message) : Exception(message);

/// <summary>
/// Issues short-lived JWT access tokens (works standalone). Refresh-token issue/rotate/revoke is pending
/// MongoDB rebuild: previously persisted via PlatformDbContext.RefreshTokens.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options, IClock clock)
{
    public const string WorkspaceClaim = "wsp";
    public const string SecurityStampClaim = "sst";

    public string CreateAccessToken(User user, Guid? workspaceId, IEnumerable<string> permissions)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(options.Value.SigningKey));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(SecurityStampClaim, user.SecurityStamp.ToString()),
        };
        if (workspaceId is { } wid) claims.Add(new Claim(WorkspaceClaim, wid.ToString()));
        foreach (var p in permissions) claims.Add(new Claim("perm", p));
        if (user.IsSystemAdmin) claims.Add(new Claim(ClaimTypes.Role, "system_admin"));

        var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience, claims,
            clock.UtcNow.UtcDateTime, clock.UtcNow.AddMinutes(options.Value.AccessTokenMinutes).UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static NotImplementedException Pending() => new("TokenService refresh-token storage is unavailable: pending MongoDB rebuild.");

    public Task<IssuedTokens> IssueAsync(User user, Guid? workspaceId, IEnumerable<string> permissions, Guid? familyId, string? ip, string? userAgent, CancellationToken ct) => throw Pending();
    public Task<(User User, string RawRefreshToken, DateTimeOffset ExpiresAt)> RotateAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct) => throw Pending();
    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => throw Pending();
    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct) => throw Pending();
}
