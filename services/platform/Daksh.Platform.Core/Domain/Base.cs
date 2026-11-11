namespace Daksh.Platform.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Rows that belong to exactly one workspace. The DbContext filters and guards these by the current tenant.</summary>
public interface IWorkspaceScoped
{
    Guid WorkspaceId { get; set; }
}

public interface ISoftDelete
{
    DateTimeOffset? DeletedAt { get; set; }
}

public abstract class WorkspaceEntity : Entity, IWorkspaceScoped
{
    public Guid WorkspaceId { get; set; }
}
