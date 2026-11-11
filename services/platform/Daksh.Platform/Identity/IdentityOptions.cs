namespace Daksh.Platform.Identity;

public sealed class JwtOptions
{
    /// <summary>Base64-encoded symmetric signing key, at least 32 bytes.</summary>
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "daksh-gateway";
    public string Audience { get; set; } = "daksh-gateway";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class AuthPolicyOptions
{
    public int MaxFailedLogins { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 10;
}
