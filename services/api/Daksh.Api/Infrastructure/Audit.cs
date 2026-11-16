namespace Daksh.Api.Infrastructure;

public interface IAuditLog
{
    /// <summary>Records an audit event. Pending MongoDB rebuild: previously staged on DakshDbContext.AuditEvents.</summary>
    void Record(string userId, string action, string? entityType = null, string? entityId = null, string? detail = null);
}

public sealed class AuditLog : IAuditLog
{
    public void Record(string userId, string action, string? entityType = null, string? entityId = null, string? detail = null) { }
}
