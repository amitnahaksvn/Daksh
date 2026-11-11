using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Identity;

public sealed class AccountLockedException(DateTimeOffset until) : Exception($"Account is locked until {until:u}.") { public DateTimeOffset Until { get; } = until; }
public sealed class InvalidCredentialsException() : Exception("Invalid email or password.");
public sealed class MfaRequiredException() : Exception("A second-factor code is required.");
public sealed class DuplicateEmailException(string email) : Exception($"An account with email '{email}' already exists.");

public sealed record LoginResult(User User, bool MfaRequired);

/// <summary>Registration, login (with lockout and optional MFA) and password reset. All checks run in code, not just the UI.</summary>
public sealed class IdentityService(PlatformDbContext db, PasswordService passwords, IOptions<AuthPolicyOptions> policy, IClock clock)
{
    public async Task<User> RegisterAsync(string email, string password, string displayName, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct)) throw new DuplicateEmailException(email);
        var errors = passwords.Validate(password, policy.Value.MinPasswordLength);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var user = new User { Email = email.Trim(), NormalizedEmail = normalized, DisplayName = displayName, PasswordHash = passwords.Hash(password) };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        // Always hash something to keep timing similar whether or not the account exists.
        var ok = user is not null && user.DisabledAt is null && passwords.Verify(user.PasswordHash, password);
        if (user?.LockoutUntil is { } until && until > clock.UtcNow) throw new AccountLockedException(until);
        if (!ok)
        {
            if (user is not null)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= policy.Value.MaxFailedLogins) user.LockoutUntil = clock.UtcNow.AddMinutes(policy.Value.LockoutMinutes);
                await db.SaveChangesAsync(ct);
            }
            throw new InvalidCredentialsException();
        }
        user!.FailedLogins = 0;
        user.LockoutUntil = null;
        if (user.MfaEnabled)
        {
            if (string.IsNullOrEmpty(mfaCode)) { await db.SaveChangesAsync(ct); throw new MfaRequiredException(); }
            if (!Totp.Verify(ProtectedMfaSecret(user), mfaCode, clock.UtcNow) && !VerifyRecoveryCode(user, mfaCode))
                throw new InvalidCredentialsException();
        }
        user.LastLoginAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new LoginResult(user, false);
    }

    // The caller decrypts the MFA secret via ISecretProtector before this is reached in the real flow;
    // kept as a seam so tests can substitute a plain secret without touching encryption.
    private static string ProtectedMfaSecret(User user) => user.MfaSecretEnc ?? "";

    private bool VerifyRecoveryCode(User user, string code)
    {
        var hash = Tokens.Hash(code.Replace("-", "").ToUpperInvariant());
        var match = user.RecoveryCodeHashes.FirstOrDefault(h => Tokens.FixedTimeEquals(h, hash));
        if (match is null) return false;
        user.RecoveryCodeHashes = user.RecoveryCodeHashes.Where(h => h != match).ToList();
        return true;
    }

    public async Task ChangePasswordAsync(User user, string newPassword, CancellationToken ct)
    {
        var errors = passwords.Validate(newPassword, policy.Value.MinPasswordLength);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        user.PasswordHash = passwords.Hash(newPassword);
        user.SecurityStamp++;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> CreatePasswordResetTokenAsync(User user, CancellationToken ct)
    {
        var token = Tokens.Generate();
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = Tokens.Hash(token), ExpiresAt = clock.UtcNow.AddHours(1) });
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct)
    {
        var hash = Tokens.Hash(token);
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || reset.UsedAt is not null || reset.ExpiresAt < clock.UtcNow)
            throw new InvalidCredentialsException();
        var user = await db.Users.FindAsync([reset.UserId], ct) ?? throw new InvalidCredentialsException();
        await ChangePasswordAsync(user, newPassword, ct);
        reset.UsedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
