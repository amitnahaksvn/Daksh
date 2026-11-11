namespace Daksh.Platform.Data;

/// <summary>
/// Who the current unit of work acts for. One instance per DI scope. HTTP requests set it from the authenticated
/// principal and the requested workspace; background workers set it explicitly for each job.
/// </summary>
public sealed class TenantContext
{
    public Guid? WorkspaceId { get; private set; }
    public Guid? UserId { get; private set; }
    /// <summary>System scope sees every workspace. Only background workers and bootstrap code may use it.</summary>
    public bool IsSystem { get; private set; }
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();
    public int RoleRank { get; private set; }
    public string ActorType { get; private set; } = "user";
    public string? ActorId { get; private set; }
    public string? Ip { get; set; }
    public string? CorrelationId { get; set; }

    public void SetUser(Guid userId, string actorType = "user", string? actorId = null)
    {
        UserId = userId; ActorType = actorType; ActorId = actorId ?? userId.ToString();
    }

    public void SetWorkspace(Guid workspaceId, IEnumerable<string> permissions, int roleRank)
    {
        WorkspaceId = workspaceId; Permissions = permissions.ToHashSet(); RoleRank = roleRank; IsSystem = false;
    }

    /// <summary>Acts as the platform itself inside one workspace (scheduler, runtime worker).</summary>
    public void SetWorkspaceAsSystem(Guid workspaceId, Guid? userId = null, string actorType = "system")
    {
        WorkspaceId = workspaceId; UserId = userId; IsSystem = false; ActorType = actorType; ActorId = actorType;
        Permissions = Identity.Permissions.All.Keys.ToHashSet(); RoleRank = 1000;
    }

    public void SetSystem()
    {
        IsSystem = true; WorkspaceId = null; ActorType = "system"; ActorId = "system";
    }

    public bool Has(string permission) => IsSystem || Permissions.Contains(permission);

    public Guid RequireWorkspace() => WorkspaceId ?? throw new InvalidOperationException("No workspace is selected for this operation.");
    public Guid RequireUser() => UserId ?? throw new InvalidOperationException("No user is associated with this operation.");
}

public sealed class TenantViolationException(string message) : Exception(message);
