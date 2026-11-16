using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Security;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Identity;

public sealed class AccountLockedException(DateTimeOffset until) : Exception($"Account is locked until {until:u}.") { public DateTimeOffset Until { get; } = until; }
public sealed class InvalidCredentialsException() : Exception("Invalid email or password.");
public sealed class MfaRequiredException() : Exception("A second-factor code is required.");
public sealed class DuplicateEmailException(string email) : Exception($"An account with email '{email}' already exists.");

public sealed record LoginResult(User User, bool MfaRequired);

/// <summary>
/// Pending MongoDB rebuild: previously handled registration, login (lockout + optional MFA) and password
/// reset against PlatformDbContext. Every member throws until the Mongo-backed implementation is written.
/// </summary>
public sealed class IdentityService
{
    private static NotImplementedException Pending() => new("IdentityService is unavailable: pending MongoDB rebuild.");

    public Task<User> RegisterAsync(string email, string password, string displayName, CancellationToken ct) => throw Pending();
    public Task<LoginResult> LoginAsync(string email, string password, string? mfaCode, CancellationToken ct) => throw Pending();
    public Task ChangePasswordAsync(User user, string newPassword, CancellationToken ct) => throw Pending();
    public Task<string> CreatePasswordResetTokenAsync(User user, CancellationToken ct) => throw Pending();
    public Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct) => throw Pending();
}
