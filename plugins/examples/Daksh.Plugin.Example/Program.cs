using System.Text.Json;
using Daksh.Plugin.Sdk;

namespace Daksh.Plugin.Example;

/// <summary>
/// Reference plugin (PL-009): a minimal, real tool plugin showing the full lifecycle — manifest, process
/// isolation via stdio JSON-RPC, and one tool implementation. See docs/PLUGIN_DEVELOPMENT.md.
/// </summary>
public sealed class ExampleTimePlugin : DakshPlugin
{
    public override string Name => "example-time";
    public override string Version => "1.0.0";

    public override Task<ToolInvokeResult> InvokeToolAsync(string name, JsonElement arguments, ToolInvokeContext context, CancellationToken ct)
    {
        if (name != "current_time") return Task.FromResult(new ToolInvokeResult(false, $"Unknown tool '{name}'."));
        var tzName = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
            ? tz.GetString() : null;
        TimeZoneInfo zone;
        try { zone = string.IsNullOrWhiteSpace(tzName) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tzName); }
        catch (TimeZoneNotFoundException) { return Task.FromResult(new ToolInvokeResult(false, $"Unknown time zone '{tzName}'.")); }
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return Task.FromResult(new ToolInvokeResult(true, $"{now:yyyy-MM-dd HH:mm:ss zzz} ({zone.Id})"));
    }
}

public static class Program
{
    public static Task Main() => PluginHost.RunAsync(new ExampleTimePlugin());
}
