using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Plugin.Sdk;

/// <summary>
/// Wire protocol between the Gateway and a plugin process: one JSON object per line on stdin/stdout (JSON-RPC 2.0
/// shape). Anything the plugin writes to stderr is captured as its log. Methods the Gateway calls:
/// initialize, tool.invoke, hook.invoke, health, shutdown.
/// </summary>
public static class PluginProtocol
{
    public const string Version = "1.0";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public sealed record RpcRequest(string Jsonrpc, long Id, string Method, JsonElement? Params);
public sealed record RpcError(int Code, string Message);
public sealed record RpcResponse(string Jsonrpc, long Id, JsonElement? Result, RpcError? Error);

public sealed record InitializeParams(string ProtocolVersion, string PlatformVersion, Dictionary<string, string> Config, Dictionary<string, string> Secrets);
public sealed record InitializeResult(string Name, string Version, string ProtocolVersion);

public sealed record ToolInvokeContext(Guid WorkspaceId, Guid AgentId, Guid SessionId, Guid? UserId);
public sealed record ToolInvokeParams(string Name, JsonElement Arguments, ToolInvokeContext Context);
public sealed record ToolInvokeResult(bool Ok, string Content);

public sealed record HookInvokeParams(string Event, JsonElement Payload);
/// <summary>Action "block" stops the operation the hook was called for (only honoured for "before" events).</summary>
public sealed record HookInvokeResult(string Action = "continue", string? Reason = null);

public sealed record HealthResult(bool Ok, string? Detail = null);

public static class HookEvents
{
    public const string BeforeToolCall = "tool.before";
    public const string AfterToolCall = "tool.after";
    public const string MessageReceived = "message.received";
    public const string RunFinished = "run.finished";
    public static readonly string[] All = [BeforeToolCall, AfterToolCall, MessageReceived, RunFinished];
}

/// <summary>The manifest file every plugin ships: daksh.plugin.json.</summary>
public sealed class PluginManifest
{
    public const string FileName = "daksh.plugin.json";

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Author { get; set; }
    public string? License { get; set; }
    public string ProtocolVersion { get; set; } = PluginProtocol.Version;
    public PluginEntry Entry { get; set; } = new();
    public PluginCompatibility Compatibility { get; set; } = new();
    public List<PluginDependency> Dependencies { get; set; } = [];
    /// <summary>network, fs:read, fs:write. Shown to the administrator at install time; nothing is granted implicitly.</summary>
    public List<string> Permissions { get; set; } = [];
    public List<PluginSecret> Secrets { get; set; } = [];
    public List<PluginConfigField> Config { get; set; } = [];
    public PluginCapabilities Capabilities { get; set; } = new();
}

public sealed class PluginEntry
{
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

public sealed class PluginCompatibility
{
    public string? MinPlatform { get; set; }
    public string? MaxPlatform { get; set; }
    public List<string> Os { get; set; } = [];
}

public sealed class PluginDependency
{
    public string Key { get; set; } = "";
    public string? MinVersion { get; set; }
}

public sealed class PluginSecret
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
}

public sealed class PluginConfigField
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Default { get; set; }
    public bool Required { get; set; }
}

public sealed class PluginCapabilities
{
    public List<PluginToolSpec> Tools { get; set; } = [];
    public List<string> Hooks { get; set; } = [];
    /// <summary>Declared for discovery; channel and provider plugins are not executed by this platform version.</summary>
    public List<string> Channels { get; set; } = [];
    public List<string> Providers { get; set; } = [];
}

public sealed class PluginToolSpec
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>low | medium | high | critical</summary>
    public string Risk { get; set; } = "medium";
    public JsonElement InputSchema { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
