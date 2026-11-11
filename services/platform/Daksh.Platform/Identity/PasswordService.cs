using Microsoft.AspNetCore.Identity;

namespace Daksh.Platform.Identity;

/// <summary>Wraps ASP.NET Core's PBKDF2 hasher (100,000+ iterations, versioned format) behind a small interface.</summary>
public sealed class PasswordService
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object Dummy = new();

    public string Hash(string password) => _hasher.HashPassword(Dummy, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(Dummy, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;

    public IReadOnlyList<string> Validate(string password, int minLength)
    {
        var errors = new List<string>();
        if (password.Length < minLength) errors.Add($"Password must be at least {minLength} characters.");
        if (!password.Any(char.IsLetter)) errors.Add("Password must contain a letter.");
        if (!password.Any(char.IsDigit)) errors.Add("Password must contain a digit.");
        return errors;
    }
}
