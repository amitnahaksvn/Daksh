using Daksh.Platform.Tools;

namespace Daksh.Platform.Domain;

public sealed class Agent : WorkspaceEntity, ISoftDelete
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string Personality { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? DeletedAt { get; set; }
    public AgentConfiguration? Configuration { get; set; }
    public List<AgentInstruction> Instructions { get; set; } = [];
    public uint Version { get; set; }
}

public sealed class AgentLimits
{
    public int MaxSteps { get; set; } = 24;
    public int MaxToolCallsPerStep { get; set; } = 8;
    public int MaxOutputTokens { get; set; } = 4096;
    public long MaxTokensPerRun { get; set; } = 400_000;
    public int RunTimeoutSeconds { get; set; } = 900;
    public int ModelTimeoutSeconds { get; set; } = 180;
    public int MaxConcurrentRuns { get; set; } = 4;
    public int MaxDelegationDepth { get; set; } = 3;
    public int MaxParallelChildren { get; set; } = 4;
    public int MaxRetriesPerModelCall { get; set; } = 2;
}

public sealed class AgentMemorySettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoExtract { get; set; } = true;
    public int RecallTopK { get; set; } = 5;
    public double RecallMinScore { get; set; } = 0.15;
    /// <summary>Scopes this agent may read: agent, user, workspace, shared.</summary>
    public List<string> ReadScopes { get; set; } = ["agent", "user", "workspace"];
    public string WriteScope { get; set; } = "agent";
}

public sealed class AgentSessionSettings
{
    /// <summary>followup | collect | steer | interrupt</summary>
    public string QueueMode { get; set; } = "followup";
    public int QueueCap { get; set; } = 20;
    public int DebounceMs { get; set; }
    /// <summary>main | per-peer | per-channel-peer | per-account-channel-peer</summary>
    public string DmScope { get; set; } = "per-channel-peer";
    /// <summary>none | daily | idle</summary>
    public string ResetMode { get; set; } = "none";
    public int ResetAtHour { get; set; } = 4;
    public int ResetIdleMinutes { get; set; } = 240;
    /// <summary>Fraction of the context window at which older turns are summarised.</summary>
    public double CompactionThreshold { get; set; } = 0.7;
    public int KeepRecentMessages { get; set; } = 12;
}

public sealed class AgentApprovalPolicy
{
    /// <summary>Tool calls at or above this risk need a human decision. "never" disables risk-based approval.</summary>
    public string RiskThreshold { get; set; } = "high";
    public List<string> AlwaysRequire { get; set; } = [];
    public List<string> AutoApprove { get; set; } = [];
    public int ExpiryMinutes { get; set; } = 60;
}

public sealed class AgentConfiguration : WorkspaceEntity
{
    public Guid AgentId { get; set; }
    /// <summary>"providerName/modelId".</summary>
    public string? DefaultModel { get; set; }
    public List<string> FallbackModels { get; set; } = [];
    public double? Temperature { get; set; }
    public ToolPolicySpec ToolPolicy { get; set; } = new();
    public List<Guid> SkillIds { get; set; } = [];
    public AgentMemorySettings Memory { get; set; } = new();
    public AgentLimits Limits { get; set; } = new();
    public AgentSessionSettings Session { get; set; } = new();
    public AgentApprovalPolicy Approval { get; set; } = new();
    /// <summary>off | process | container. Where exec/code tools run for this agent.</summary>
    public string SandboxMode { get; set; } = "container";
    /// <summary>none | ro | rw</summary>
    public string WorkspaceAccess { get; set; } = "rw";
    public bool HeartbeatEnabled { get; set; }
    public int HeartbeatMinutes { get; set; } = 30;
}

public sealed class AgentInstruction : WorkspaceEntity
{
    /// <summary>Null means a workspace-level instruction applied to every agent.</summary>
    public Guid? AgentId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>system | persona | user | boot | tools</summary>
    public string Kind { get; set; } = "system";
    public string Content { get; set; } = "";
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Which agents an agent may hand work to or message.</summary>
public sealed class AgentDelegation : WorkspaceEntity
{
    public Guid AgentId { get; set; }
    public Guid TargetAgentId { get; set; }
    public bool CanDelegate { get; set; } = true;
    public bool CanMessage { get; set; } = true;
    public bool RequireApproval { get; set; }
}

public sealed class ModelProvider : WorkspaceEntity
{
    /// <summary>Workspace-unique key used in "provider/model" references.</summary>
    public string Name { get; set; } = "";
    /// <summary>openai | anthropic | ollama | openai-compatible | anthropic-compatible | scripted</summary>
    public string Kind { get; set; } = "";
    public string? BaseUrl { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>unknown | healthy | degraded | down</summary>
    public string HealthStatus { get; set; } = "unknown";
    public DateTimeOffset? HealthCheckedAt { get; set; }
    public string? HealthError { get; set; }
    public List<ProviderCredential> Credentials { get; set; } = [];
    public List<Model> Models { get; set; } = [];
}

public sealed class ProviderCredential : WorkspaceEntity
{
    public Guid ProviderId { get; set; }
    public string Label { get; set; } = "default";
    public string SecretEnc { get; set; } = "";
    public int Priority { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public int ErrorCount { get; set; }
    public string? DisabledReason { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class ModelCapabilities
{
    public bool Chat { get; set; } = true;
    public bool Streaming { get; set; } = true;
    public bool Tools { get; set; }
    public bool Vision { get; set; }
    public bool StructuredOutput { get; set; }
    public bool Embeddings { get; set; }
    public bool AudioInput { get; set; }
    public bool AudioOutput { get; set; }
    public bool ImageGeneration { get; set; }
}

public sealed class Model : WorkspaceEntity
{
    public Guid ProviderId { get; set; }
    public ModelProvider? Provider { get; set; }
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ModelCapabilities Capabilities { get; set; } = new();
    public int ContextWindow { get; set; } = 32_000;
    public int MaxOutputTokens { get; set; } = 4096;
    /// <summary>Price in USD per million tokens. Zero for local models.</summary>
    public decimal InputPricePerMTok { get; set; }
    public decimal OutputPricePerMTok { get; set; }
    public bool Enabled { get; set; } = true;
}
