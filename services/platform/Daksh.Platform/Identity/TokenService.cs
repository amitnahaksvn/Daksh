using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Daksh.Platform.Identity;

public sealed record IssuedTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt, DateTimeOffset RefreshExpiresAt);

public sealed class SecurityStampMismatchException(string message) : Exception(message);
public sealed class RefreshTokenReuseException(string message) : Exception(message);

/// <summary>
/// Issues short-lived JWT access tokens and rotating opaque refresh tokens. A refresh token belongs to a "family";
/// using one that was already rotated away revokes the whole family (reuse / theft detection, SEC-004).
/// </summary>
public sealed class TokenService(PlatformDbContext db, IOptions<JwtOptions> options, IClock clock)
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

    public async Task<IssuedTokens> IssueAsync(User user, Guid? workspaceId, IEnumerable<string> permissions, Guid? familyId, string? ip, string? userAgent, CancellationToken ct)
    {
        var access = CreateAccessToken(user, workspaceId, permissions);
        var refresh = Tokens.Generate();
        var expires = clock.UtcNow.AddDays(options.Value.RefreshTokenDays);
        var token = new RefreshToken
        {
            UserId = user.Id, TokenHash = Tokens.Hash(refresh), FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = expires, Ip = ip, UserAgent = userAgent,
        };
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new IssuedTokens(access, refresh, clock.UtcNow.AddMinutes(options.Value.AccessTokenMinutes), expires);
    }

    /// <summary>Validates and rotates a refresh token, returning the new raw token (shown to the caller once, never stored). Throws <see cref="RefreshTokenReuseException"/> if it was already rotated.</summary>
    public async Task<(User User, string RawRefreshToken, DateTimeOffset ExpiresAt)> RotateAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct)
    {
        var hash = Tokens.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new SecurityException("Refresh token is not recognized.");
        if (token.RevokedAt is not null)
        {
            // This token was already rotated or revoked: someone is replaying an old token. Kill the whole family.
            await RevokeFamilyAsync(token.FamilyId, ct);
            throw new RefreshTokenReuseException("This refresh token has already been used. All sessions in this family were signed out.");
        }
        if (token.ExpiresAt < clock.UtcNow) throw new SecurityException("Refresh token has expired.");

        var user = await db.Users.FindAsync([token.UserId], ct) ?? throw new SecurityException("User no longer exists.");
        var raw = Tokens.Generate();
        var expires = clock.UtcNow.AddDays(options.Value.RefreshTokenDays);
        var newToken = new RefreshToken
        {
            UserId = user.Id, TokenHash = Tokens.Hash(raw), FamilyId = token.FamilyId,
            ExpiresAt = expires, Ip = ip, UserAgent = userAgent,
        };
        token.RevokedAt = clock.UtcNow;
        token.ReplacedById = newToken.Id;
        db.RefreshTokens.Add(newToken);
        await db.SaveChangesAsync(ct);
        return (user, raw, expires);
    }

    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.RevokedAt = clock.UtcNow;
        var user = await db.Users.FindAsync([userId], ct);
        if (user is not null) user.SecurityStamp++;
        await db.SaveChangesAsync(ct);
    }

    private sealed class SecurityException(string message) : Exception(message);
}
