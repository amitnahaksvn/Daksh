namespace Daksh.Platform.Domain;

public enum SessionKind { Direct, Group, Channel, Subagent, Automation, Test }
public enum SessionStatus { Active, Archived }

public sealed class Session : WorkspaceEntity, ISoftDelete
{
    public Guid AgentId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string Title { get; set; } = "New session";
    public SessionKind Kind { get; set; } = SessionKind.Direct;
    /// <summary>Stable routing key for channel sessions; unique per workspace when set.</summary>
    public string? Key { get; set; }
    public Guid? ChannelAccountId { get; set; }
    public string? PeerId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    /// <summary>Summary of compacted messages (sequence numbers up to <see cref="SummaryUpToSeq"/>).</summary>
    public string? Summary { get; set; }
    public long SummaryUpToSeq { get; set; }
    /// <summary>Incremented by a reset. Only messages of the current epoch are sent to the model.</summary>
    public int Epoch { get; set; }
    public Guid? ParentSessionId { get; set; }
    public long? BranchedAtSeq { get; set; }
    /// <summary>Visible to every workspace member who may read sessions, not only owner and participants.</summary>
    public bool Shared { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
    public long NextSeq { get; set; } = 1;
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class SessionParticipant : WorkspaceEntity
{
    public Guid SessionId { get; set; }
    public Guid? UserId { get; set; }
    public string? ExternalIdentity { get; set; }
    /// <summary>owner | member | viewer</summary>
    public string Role { get; set; } = "member";
}

public sealed class Message : WorkspaceEntity
{
    public Guid SessionId { get; set; }
    public long Seq { get; set; }
    /// <summary>user | assistant | tool | system</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    /// <summary>JSON array of {id,name,arguments} for assistant messages that call tools.</summary>
    public string? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string? AuthorName { get; set; }
    public string? Model { get; set; }
    public int Epoch { get; set; }
    public bool Compacted { get; set; }
    /// <summary>Content came from outside (web page, file, channel peer, tool output) and must never be treated as instructions.</summary>
    public bool Untrusted { get; set; }
    /// <summary>-1, 0, 1</summary>
    public int Feedback { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
    public List<MessageAttachment> Attachments { get; set; } = [];
}

public sealed class MessageAttachment : WorkspaceEntity
{
    public Guid MessageId { get; set; }
    public Guid FileId { get; set; }
}

public enum ExecutionStatus { Queued, Running, WaitingApproval, Succeeded, Failed, Cancelled }

public sealed class Execution : WorkspaceEntity
{
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ParentExecutionId { get; set; }
    public Guid RootExecutionId { get; set; }
    public int Depth { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;
    /// <summary>chat | channel | automation | delegation | webhook | api | heartbeat</summary>
    public string Trigger { get; set; } = "chat";
    /// <summary>Sequence number of the message that started the run.</summary>
    public long InputSeq { get; set; }
    public string? ModelOverride { get; set; }
    public string? ModelUsed { get; set; }
    public int Step { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? FinalText { get; set; }
    /// <summary>A worker owns the run until this time; after it another worker may resume it.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? WorkerId { get; set; }
    public bool CancelRequested { get; set; }
    public int Resumes { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public long NextEventSeq { get; set; } = 1;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    /// <summary>Where the final answer must be delivered besides the session (channel reply, webhook...). JSON.</summary>
    public string? Delivery { get; set; }
    public uint Version { get; set; }
}

public sealed class ExecutionEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid SessionId { get; set; }
    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public string Data { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ExecutionCheckpoint : WorkspaceEntity
{
    public Guid ExecutionId { get; set; }
    public int Step { get; set; }
    /// <summary>JSON: phase, pending tool calls, steering messages consumed, loop-detector state.</summary>
    public string State { get; set; } = "{}";
}

public enum ToolExecutionStatus { Pending, WaitingApproval, Running, Succeeded, Failed, Denied, Interrupted, Cancelled }

public sealed class ToolExecution : WorkspaceEntity
{
    public Guid ExecutionId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    public string ToolCallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Arguments { get; set; } = "{}";
    public string? Result { get; set; }
    public ToolExecutionStatus Status { get; set; } = ToolExecutionStatus.Pending;
    public string Risk { get; set; } = "low";
    public int Attempts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public Guid? ApprovalId { get; set; }
}

/// <summary>Registry mirror so the UI and audits can show which tools existed, from which source and at which version.</summary>
public sealed class ToolDefinition : Entity
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public string Risk { get; set; } = "low";
    /// <summary>core | plugin:&lt;key&gt;</summary>
    public string Source { get; set; } = "core";
    public string InputSchema { get; set; } = "{}";
    public string? OutputSchema { get; set; }
    public List<string> Permissions { get; set; } = [];
    public int TimeoutSeconds { get; set; }
    public int MaxRetries { get; set; }
    public bool Available { get; set; } = true;
    public string? UnavailableReason { get; set; }
}

/// <summary>Workspace-level tool rule, optionally narrowed to an agent or a user. Deny always wins.</summary>
public sealed class ToolPermission : WorkspaceEntity
{
    public Guid? AgentId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Tool name, wildcard or "group:name".</summary>
    public string Pattern { get; set; } = "";
    /// <summary>allow | deny | approve</summary>
    public string Effect { get; set; } = "deny";
}

public enum ApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }

public sealed class Approval : WorkspaceEntity
{
    public Guid? ExecutionId { get; set; }
    public Guid? ToolExecutionId { get; set; }
    public Guid? SessionId { get; set; }
    /// <summary>tool | delegation | device | plugin</summary>
    public string Kind { get; set; } = "tool";
    public string Title { get; set; } = "";
    /// <summary>JSON with the exact action (tool, arguments) the approver is deciding on.</summary>
    public string Detail { get; set; } = "{}";
    public string Risk { get; set; } = "high";
    public Guid? RequestedByUserId { get; set; }
    public Guid? RequestedByAgentId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public uint Version { get; set; }
}

public sealed class UsageRecord : WorkspaceEntity
{
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? RootExecutionId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>chat | embedding | summary | extraction | speech | image</summary>
    public string Purpose { get; set; } = "chat";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long LatencyMs { get; set; }
    public long? TimeToFirstTokenMs { get; set; }
    public bool Success { get; set; } = true;
    public bool Estimated { get; set; }
    public string? ErrorKind { get; set; }
}

public sealed class CostRecord : WorkspaceEntity
{
    public Guid UsageRecordId { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public decimal InputPricePerMTok { get; set; }
    public decimal OutputPricePerMTok { get; set; }
}

public sealed class StoredFile : WorkspaceEntity, ISoftDelete
{
    public Guid? OwnerUserId { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Path relative to the storage root. Never taken from user input.</summary>
    public string StoragePath { get; set; } = "";
    /// <summary>attachment | generated | download | screenshot | audio | import | backup</summary>
    public string Purpose { get; set; } = "attachment";
    /// <summary>not_scanned | clean | infected | error</summary>
    public string ScanStatus { get; set; } = "not_scanned";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
