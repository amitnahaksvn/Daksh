namespace Daksh.Platform.Domain;

public sealed class Memory : WorkspaceEntity, ISoftDelete
{
    /// <summary>user | agent | workspace | session | shared</summary>
    public string Scope { get; set; } = "agent";
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? SessionId { get; set; }
    /// <summary>semantic | episodic | structured | document</summary>
    public string Kind { get; set; } = "semantic";
    /// <summary>Optional stable key for structured memories ("user.timezone").</summary>
    public string? Key { get; set; }
    public string Content { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public double Importance { get; set; } = 0.5;
    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<MemorySource> Sources { get; set; } = [];
}

/// <summary>One embedding per memory and model. The vector column itself is managed with SQL (pgvector).</summary>
public sealed class MemoryEmbedding : WorkspaceEntity
{
    public Guid MemoryId { get; set; }
    public string Model { get; set; } = "";
    public int Dimensions { get; set; }
}

public sealed class MemorySource : WorkspaceEntity
{
    public Guid MemoryId { get; set; }
    /// <summary>message | file | manual | import | tool | consolidation</summary>
    public string SourceType { get; set; } = "manual";
    public string? SourceId { get; set; }
    public string? Excerpt { get; set; }
}

public sealed class Skill : Entity, ISoftDelete
{
    /// <summary>Null for bundled skills visible to every workspace.</summary>
    public Guid? WorkspaceId { get; set; }
    /// <summary>Set when the skill belongs to one agent only.</summary>
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Guid? CurrentVersionId { get; set; }
    /// <summary>bundled | catalog | import | authored</summary>
    public string Source { get; set; } = "authored";
    public Dictionary<string, string> Settings { get; set; } = [];
    public int UseCount { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<SkillVersion> Versions { get; set; } = [];
}

public sealed class SkillVersion : Entity
{
    public Guid SkillId { get; set; }
    public string Version { get; set; } = "1.0.0";
    /// <summary>Full SKILL.md text.</summary>
    public string Content { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Frontmatter { get; set; } = "{}";
}

public sealed class Plugin : Entity
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Manifest { get; set; } = "{}";
    public string InstallPath { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>installed | running | stopped | failed</summary>
    public string Status { get; set; } = "installed";
    /// <summary>process | container</summary>
    public string Isolation { get; set; } = "process";
    public List<string> GrantedPermissions { get; set; } = [];
    public string? LastError { get; set; }
    public string ManifestHash { get; set; } = "";
}

public sealed class PluginConfiguration : Entity
{
    public Guid PluginId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Dictionary<string, string> Settings { get; set; } = [];
    /// <summary>Encrypted JSON object of secret name to value.</summary>
    public string? SecretsEnc { get; set; }
}

public sealed class PluginLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PluginId { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChannelPolicy
{
    /// <summary>pairing | allowlist | open | disabled</summary>
    public string DmPolicy { get; set; } = "pairing";
    public List<string> AllowFrom { get; set; } = [];
    /// <summary>allowlist | open | disabled</summary>
    public string GroupPolicy { get; set; } = "allowlist";
    public List<string> AllowGroups { get; set; } = [];
    public bool RequireMention { get; set; } = true;
    public List<string> MentionPatterns { get; set; } = [];
    public bool IgnoreBots { get; set; } = true;
    public int MaxInboundPerMinutePerPeer { get; set; } = 20;
}

public sealed class Channel : WorkspaceEntity
{
    /// <summary>Adapter kind: webchat, telegram, discord, slack, whatsapp, webhook, mock, plugin:&lt;key&gt;.</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ChannelPolicy Policy { get; set; } = new();
    public List<ChannelAccount> Accounts { get; set; } = [];
}

public sealed class ChannelAccount : WorkspaceEntity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Encrypted JSON of adapter credentials.</summary>
    public string? CredentialsEnc { get; set; }
    public Dictionary<string, string> Settings { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Unguessable path segment of this account's inbound webhook URL.</summary>
    public string WebhookKey { get; set; } = "";
    public Guid? DefaultAgentId { get; set; }
}

public sealed class ChannelConnection : WorkspaceEntity
{
    public Guid ChannelAccountId { get; set; }
    /// <summary>disconnected | connecting | connected | error</summary>
    public string Status { get; set; } = "disconnected";
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
    public string? Error { get; set; }
    public int ReconnectAttempts { get; set; }
    /// <summary>Adapter cursor (update offset, resume token...) so receiving continues after a restart.</summary>
    public string? Cursor { get; set; }
}

public sealed class ChannelMessage : WorkspaceEntity
{
    public Guid ChannelAccountId { get; set; }
    /// <summary>in | out</summary>
    public string Direction { get; set; } = "in";
    public string ExternalMessageId { get; set; } = "";
    public string ExternalChatId { get; set; } = "";
    public string? ExternalSenderId { get; set; }
    public string? ExternalThreadId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? ExecutionId { get; set; }
    /// <summary>received | ignored | routed | pending | delivered | failed | dead</summary>
    public string Status { get; set; } = "received";
    public string? Reason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ChannelRoute : WorkspaceEntity
{
    public Guid? ChannelAccountId { get; set; }
    /// <summary>Exact chat/peer id, or "*" for any.</summary>
    public string Peer { get; set; } = "*";
    /// <summary>dm | group | any</summary>
    public string ChatType { get; set; } = "any";
    public Guid AgentId { get; set; }
    public int Priority { get; set; }
}

public sealed class ChannelIdentity : WorkspaceEntity
{
    public Guid ChannelAccountId { get; set; }
    public string ExternalSenderId { get; set; } = "";
    public string? DisplayName { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>pending | approved | blocked</summary>
    public string Status { get; set; } = "pending";
    public string? PairingCodeHash { get; set; }
    public DateTimeOffset? PairingExpiresAt { get; set; }
    public string? ReplyChatId { get; set; }
}

public sealed class AutomationRetry
{
    public int MaxAttempts { get; set; } = 3;
    public int BackoffSeconds { get; set; } = 30;
}

public sealed class Automation : WorkspaceEntity, ISoftDelete
{
    public Guid OwnerUserId { get; set; }
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>schedule | webhook | event | heartbeat</summary>
    public string TriggerKind { get; set; } = "schedule";
    /// <summary>at | every | cron</summary>
    public string? ScheduleKind { get; set; }
    public string? Cron { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public int? EverySeconds { get; set; }
    public DateTimeOffset? RunAt { get; set; }
    public string? EventType { get; set; }
    /// <summary>JSON condition evaluated against the trigger payload.</summary>
    public string? Condition { get; set; }
    /// <summary>agent_prompt | message | tool</summary>
    public string ActionKind { get; set; } = "agent_prompt";
    /// <summary>JSON: {prompt} | {text} | {tool, arguments}</summary>
    public string Action { get; set; } = "{}";
    /// <summary>JSON: {kind: session|channel|webhook|none, ...}</summary>
    public string Delivery { get; set; } = "{\"kind\":\"session\"}";
    public AutomationRetry Retry { get; set; } = new();
    /// <summary>active | paused | completed | cancelled</summary>
    public string Status { get; set; } = "active";
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public Guid? SessionId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint Version { get; set; }
}

public enum JobStatus { Pending, Running, Succeeded, Failed, Dead, Cancelled }

/// <summary>One due occurrence of an automation. Unique per (automation, slot) so a slot can never run twice.</summary>
public sealed class ScheduledJob : WorkspaceEntity
{
    public Guid AutomationId { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? WorkerId { get; set; }
    /// <summary>JSON payload of the trigger (webhook body, event data).</summary>
    public string? Payload { get; set; }
    public string? LastError { get; set; }
}

public sealed class JobExecution : WorkspaceEntity
{
    public Guid ScheduledJobId { get; set; }
    public Guid AutomationId { get; set; }
    public int Attempt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
    public Guid? ExecutionId { get; set; }
    public string Log { get; set; } = "";
    public bool Delivered { get; set; }
}

public sealed class Webhook : WorkspaceEntity
{
    public Guid? AutomationId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Public identifier in the URL: /hooks/{key}.</summary>
    public string Key { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? HmacSecretEnc { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastCalledAt { get; set; }
    public long CallCount { get; set; }
}

public sealed class WebhookDelivery : WorkspaceEntity
{
    public Guid WebhookId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public Guid? ScheduledJobId { get; set; }
}

public sealed class Device : WorkspaceEntity
{
    public string Name { get; set; } = "";
    /// <summary>windows | macos | linux | ios | android</summary>
    public string Platform { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    /// <summary>pending_approval | active | revoked. A device is created in pending_approval when a node
    /// redeems a pairing code; a user must explicitly approve it before it can connect to the node hub.</summary>
    public string Status { get; set; } = "pending_approval";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Online { get; set; }
    public string? NodeVersion { get; set; }
    public string? ProtocolVersion { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public List<DeviceCapability> Capabilities { get; set; } = [];
}

public sealed class DevicePairing : WorkspaceEntity
{
    public string CodeHash { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public List<string> RequestedCapabilities { get; set; } = [];
    /// <summary>pending | approved | rejected | expired | claimed</summary>
    public string Status { get; set; } = "pending";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid? DeviceId { get; set; }
    /// <summary>Hash of the secret the node polls with to collect its token once approved.</summary>
    public string ClaimSecretHash { get; set; } = "";
}

public sealed class DeviceCapability : WorkspaceEntity
{
    public Guid DeviceId { get; set; }
    /// <summary>For example system.info, notify.show, screen.capture, fs.read, system.run.</summary>
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The device declared it; a user must still allow it.</summary>
    public bool Allowed { get; set; }
    public bool RequiresApproval { get; set; }
}

public sealed class DeviceEvent : WorkspaceEntity
{
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public string Data { get; set; } = "{}";
}
