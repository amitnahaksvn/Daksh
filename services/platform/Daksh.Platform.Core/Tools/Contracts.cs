using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Platform.Tools;

public enum ToolRisk { Low = 0, Medium = 1, High = 2, Critical = 3 }

public sealed record ToolDescriptor(
    string Name,
    string Description,
    string Group,
    JsonElement InputSchema,
    ToolRisk Risk = ToolRisk.Low,
    string Version = "1.0.0",
    JsonElement? OutputSchema = null,
    IReadOnlyList<string>? Permissions = null,
    int TimeoutSeconds = 60,
    int MaxRetries = 0,
    /// <summary>May run at the same time as other calls of the same step.</summary>
    bool ParallelSafe = true,
    /// <summary>Safe to run again after an interruption (no side effects, or repeatable ones).</summary>
    bool Idempotent = false,
    /// <summary>The result is outside content and is wrapped as untrusted before the model sees it.</summary>
    bool ReturnsUntrustedContent = false,
    string Source = "core");

public sealed record ToolResult(bool Ok, string Content, IReadOnlyList<Guid>? FileIds = null)
{
    public static ToolResult Success(string content, IReadOnlyList<Guid>? files = null) => new(true, content, files);
    public static ToolResult Fail(string message) => new(false, message);
    public static ToolResult Json(object value) => new(true, JsonSerializer.Serialize(value, JsonDefaults.Options));
}

/// <summary>Everything a tool may know about the call. Tools get scoped services from <see cref="Services"/>.</summary>
public sealed class ToolContext
{
    public required Guid WorkspaceId { get; init; }
    public required Guid AgentId { get; init; }
    public Guid? UserId { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid ExecutionId { get; init; }
    public required Guid RootExecutionId { get; init; }
    public required string ToolCallId { get; init; }
    public int Depth { get; init; }
    /// <summary>Absolute directory the agent's file and shell tools are confined to.</summary>
    public required string WorkspaceDirectory { get; init; }
    /// <summary>none | ro | rw</summary>
    public string WorkspaceAccess { get; init; } = "rw";
    /// <summary>off | process | container</summary>
    public string SandboxMode { get; init; } = "container";
    public required IServiceProvider Services { get; init; }
    /// <summary>Reports progress text shown under the running tool in the UI.</summary>
    public Func<string, Task> Progress { get; init; } = _ => Task.CompletedTask;
}

public interface ITool
{
    ToolDescriptor Descriptor { get; }

    /// <summary>Risk of this particular call; lets a tool raise its risk for dangerous arguments.</summary>
    ToolRisk AssessRisk(JsonElement arguments) => Descriptor.Risk;

    /// <summary>Null when usable, otherwise why not (missing configuration, missing runtime...).</summary>
    string? Unavailable(IServiceProvider services) => null;

    Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema, Options);

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string Required(this JsonElement e, string name) =>
        e.Str(name) is { Length: > 0 } s ? s : throw new ToolArgumentException($"Argument '{name}' is required.");
}

public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>Minimal JSON-schema check for tool arguments: object shape, required properties, primitive types, enums.</summary>
public static class SchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement value, string path = "$")
    {
        var errors = new List<string>();
        Check(schema, value, path, errors);
        return errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var ok = t.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => true
            };
            if (!ok) { errors.Add($"{path}: expected {t.GetString()}"); return; }
        }
        if (schema.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.String
            && !en.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == value.GetString()))
            errors.Add($"{path}: must be one of {string.Join(", ", en.EnumerateArray().Select(x => x.ToString()))}");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var r in req.EnumerateArray())
                    if (!value.TryGetProperty(r.GetString()!, out var rv) || rv.ValueKind == JsonValueKind.Null)
                        errors.Add($"{path}.{r.GetString()}: is required");
            if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                    if (value.TryGetProperty(p.Name, out var pv) && pv.ValueKind != JsonValueKind.Null)
                        Check(p.Value, pv, $"{path}.{p.Name}", errors);
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) Check(items, item, $"{path}[{i++}]", errors);
        }
    }
}

/// <summary>Per-agent tool policy. Evaluated in code before every model call; a tool that is filtered out is never offered.</summary>
public sealed class ToolPolicySpec
{
    /// <summary>minimal | chat | coding | messaging | full</summary>
    public string Profile { get; set; } = "chat";
    /// <summary>When not empty, replaces the profile's set. Names, wildcards or "group:name".</summary>
    public List<string> Allow { get; set; } = [];
    public List<string> AlsoAllow { get; set; } = [];
    public List<string> Deny { get; set; } = [];
}

public sealed record ToolRule(string Pattern, string Effect, bool AgentSpecific = false, bool UserSpecific = false);

public sealed record ToolDecision(string Name, bool Allowed, bool RequiresApproval, string Reason);

public static class ToolPolicy
{
    public static readonly IReadOnlyDictionary<string, string[]> Profiles = new Dictionary<string, string[]>
    {
        ["minimal"] = ["session_status"],
        ["chat"] = ["group:memory", "group:web", "group:sessions", "group:skills", "session_status", "image_analyze"],
        ["coding"] = ["group:fs", "group:runtime", "group:web", "group:memory", "group:sessions", "group:skills", "group:automation", "session_status", "image_analyze"],
        ["messaging"] = ["group:messaging", "group:sessions", "group:memory", "group:skills", "session_status"],
        ["full"] = ["*"],
    };

    public static bool Matches(string pattern, ToolDescriptor tool)
    {
        if (pattern.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(pattern[6..], tool.Group, StringComparison.OrdinalIgnoreCase);
        if (pattern == "*") return true;
        if (!pattern.Contains('*')) return string.Equals(pattern, tool.Name, StringComparison.OrdinalIgnoreCase);
        return Regex.IsMatch(tool.Name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Order: profile (or explicit allow) plus alsoAllow, then workspace/agent/user "allow" rules add,
    /// then every deny (agent policy or rule) removes. Deny always wins. "approve" rules force a human decision.
    /// </summary>
    public static IReadOnlyList<ToolDecision> Evaluate(IEnumerable<ToolDescriptor> tools, ToolPolicySpec spec, IReadOnlyCollection<ToolRule> rules)
    {
        if (spec.Allow.Count > 0 && spec.AlsoAllow.Count > 0)
            throw new InvalidOperationException("Tool policy cannot set both 'allow' and 'alsoAllow'.");
        var baseSet = spec.Allow.Count > 0
            ? spec.Allow
            : (Profiles.TryGetValue(spec.Profile, out var p) ? p : throw new InvalidOperationException($"Unknown tool profile '{spec.Profile}'.")).Concat(spec.AlsoAllow).ToList();

        var result = new List<ToolDecision>();
        foreach (var tool in tools)
        {
            var allowed = baseSet.Any(x => Matches(x, tool));
            var reason = allowed ? "allowed by agent policy" : "not in agent policy";
            if (!allowed && rules.Any(r => r.Effect == "allow" && Matches(r.Pattern, tool))) { allowed = true; reason = "allowed by workspace rule"; }
            if (spec.Deny.Any(x => Matches(x, tool))) { allowed = false; reason = "denied by agent policy"; }
            if (rules.Any(r => r.Effect == "deny" && Matches(r.Pattern, tool))) { allowed = false; reason = "denied by workspace rule"; }
            var approve = allowed && rules.Any(r => r.Effect == "approve" && Matches(r.Pattern, tool));
            result.Add(new ToolDecision(tool.Name, allowed, approve, reason));
        }
        return result;
    }

    public static ToolRisk ParseRisk(string? s) => s?.ToLowerInvariant() switch
    {
        "low" => ToolRisk.Low, "medium" => ToolRisk.Medium, "high" => ToolRisk.High, "critical" => ToolRisk.Critical,
        _ => ToolRisk.High
    };

    /// <summary>Whether a call needs a human decision under the agent's approval policy.</summary>
    public static bool NeedsApproval(string toolName, ToolRisk risk, string riskThreshold, IReadOnlyCollection<string> alwaysRequire,
        IReadOnlyCollection<string> autoApprove, bool ruleRequiresApproval, ToolDescriptor descriptor)
    {
        if (ruleRequiresApproval || alwaysRequire.Any(p => Matches(p, descriptor))) return true;
        // Critical calls can never be auto-approved by configuration.
        if (risk < ToolRisk.Critical && autoApprove.Any(p => Matches(p, descriptor))) return false;
        if (string.Equals(riskThreshold, "never", StringComparison.OrdinalIgnoreCase)) return risk >= ToolRisk.Critical;
        return risk >= ParseRisk(riskThreshold);
    }
}
