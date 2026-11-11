namespace Daksh.Platform.Domain;

public sealed class User : Entity
{
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Instance administrator: may manage users and system settings. Not a workspace role.</summary>
    public bool IsSystemAdmin { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecretEnc { get; set; }
    public List<string> RecoveryCodeHashes { get; set; } = [];
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public Dictionary<string, string> Preferences { get; set; } = [];
    /// <summary>Bumped on password change / "sign out everywhere"; embedded in access tokens.</summary>
    public int SecurityStamp { get; set; }
}

public sealed class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>All tokens descended from one login. Reuse of a rotated token revokes the whole family.</summary>
    public Guid FamilyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }
}

public sealed class PasswordResetToken : Entity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class ApiKey : WorkspaceEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>First characters of the key, shown in the UI so a key can be recognised.</summary>
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    /// <summary>Permissions granted to the key; always intersected with the owner's current permissions.</summary>
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class Role : Entity
{
    /// <summary>Null for the built-in roles shared by every workspace.</summary>
    public Guid? WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsSystem { get; set; }
    /// <summary>Higher rank may manage lower rank; equal or higher rank cannot be granted by a member.</summary>
    public int Rank { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public sealed class Permission
{
    public string Key { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string PermissionKey { get; set; } = "";
}

public sealed class Workspace : Entity, ISoftDelete
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public Dictionary<string, string> Settings { get; set; } = [];
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class WorkspaceMember : WorkspaceEntity
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public User? User { get; set; }
}

public sealed class WorkspaceInvitation : WorkspaceEntity
{
    public string Email { get; set; } = "";
    public Guid RoleId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Append-only security and data trail. Updates and deletes are rejected by a database trigger.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? WorkspaceId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>user | agent | system | device | channel | apikey</summary>
    public string ActorType { get; set; } = "user";
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
    public string? CorrelationId { get; set; }
    public bool SecurityEvent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SystemSetting : Entity
{
    /// <summary>system | workspace | user | agent | provider | channel</summary>
    public string Scope { get; set; } = "system";
    public Guid? ScopeId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>JSON value; encrypted when <see cref="IsSecret"/>.</summary>
    public string Value { get; set; } = "null";
    public bool IsSecret { get; set; }
}

/// <summary>Remembers the outcome of a client-supplied idempotency key so a retried request is not applied twice.</summary>
public sealed class IdempotencyRecord
{
    public string Scope { get; set; } = "";
    public string Key { get; set; } = "";
    public string? ResultRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
