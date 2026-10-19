using Daksh.Domain;

namespace Daksh.Api.Infrastructure;

public interface IAuditLog
{
    /// <summary>Stages an audit event on the current DbContext; persisted by the caller's SaveChanges so it commits atomically with the change.</summary>
    void Record(string userId, string action, string? entityType = null, string? entityId = null, string? detail = null);
}

public sealed class AuditLog(DakshDbContext db) : IAuditLog
{
    public void Record(string userId, string action, string? entityType = null, string? entityId = null, string? detail = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId, Action = action, EntityType = entityType, EntityId = entityId, Detail = detail
        });
}
