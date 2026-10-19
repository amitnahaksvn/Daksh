namespace Daksh.Domain;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ConversationMessage> Messages { get; set; } = new();
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = "";
    public string? Provider { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset? DueAt { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset RemindAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Set when the notification was delivered; makes the reminder handler idempotent.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }
}

/// <summary>Append-only record of security- and data-relevant actions.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrivacySettings
{
    public string UserId { get; set; } = "";
    public bool MemoryEnabled { get; set; }
    public bool AllowProviderDataSharing { get; set; }
    public int RetentionDays { get; set; } = 90;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobStatus { Pending = 0, Running = 1, Succeeded = 2, Failed = 3, DeadLetter = 4, Cancelled = 5 }

/// <summary>Durable background job. Claimed and executed by Daksh.Worker.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    /// <summary>Unique per logical operation; duplicates are rejected so handlers run once.</summary>
    public string IdempotencyKey { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Optimistic concurrency token so only one worker can claim a job.</summary>
    public int Version { get; set; }
}

public static class JobTypes
{
    public const string ReminderDue = "reminder.due";
}
