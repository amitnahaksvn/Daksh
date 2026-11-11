using System.Text.Json;

namespace Daksh.Plugin.Sdk;

/// <summary>Base class for a plugin written in .NET. Override what the plugin offers and call <see cref="PluginHost.RunAsync"/> from Main.</summary>
public abstract class DakshPlugin
{
    public abstract string Name { get; }
    public abstract string Version { get; }

    public IReadOnlyDictionary<string, string> Config { get; internal set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Secrets { get; internal set; } = new Dictionary<string, string>();

    public virtual Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual Task<ToolInvokeResult> InvokeToolAsync(string name, JsonElement arguments, ToolInvokeContext context, CancellationToken ct) =>
        Task.FromResult(new ToolInvokeResult(false, $"Unknown tool '{name}'."));

    public virtual Task<HookInvokeResult> InvokeHookAsync(string @event, JsonElement payload, CancellationToken ct) =>
        Task.FromResult(new HookInvokeResult());

    public virtual Task<HealthResult> HealthAsync(CancellationToken ct) => Task.FromResult(new HealthResult(true));

    /// <summary>Writes a log line (stderr). Never write logs to stdout: it carries the protocol.</summary>
    protected static void Log(string message) => Console.Error.WriteLine(message);
}

public static class PluginHost
{
    /// <summary>Serves the plugin protocol on the given streams until "shutdown" or end of input.</summary>
    public static async Task RunAsync(DakshPlugin plugin, TextReader? input = null, TextWriter? output = null, CancellationToken ct = default)
    {
        input ??= new StreamReader(Console.OpenStandardInput());
        output ??= new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);

        while (!ct.IsCancellationRequested && await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RpcRequest? request;
            try { request = JsonSerializer.Deserialize<RpcRequest>(line, PluginProtocol.Json); }
            catch (JsonException) { continue; }
            if (request is null) continue;

            RpcResponse response;
            try
            {
                object result = request.Method switch
                {
                    "initialize" => await Initialize(plugin, request, ct),
                    "tool.invoke" => await InvokeTool(plugin, request, ct),
                    "hook.invoke" => await InvokeHook(plugin, request, ct),
                    "health" => await plugin.HealthAsync(ct),
                    "shutdown" => new HealthResult(true),
                    _ => throw new RpcException(-32601, $"Unknown method '{request.Method}'.")
                };
                response = new RpcResponse("2.0", request.Id, JsonSerializer.SerializeToElement(result, PluginProtocol.Json), null);
            }
            catch (RpcException ex) { response = new RpcResponse("2.0", request.Id, null, new RpcError(ex.Code, ex.Message)); }
            catch (Exception ex) { response = new RpcResponse("2.0", request.Id, null, new RpcError(-32000, ex.Message)); }

            await writeLock.WaitAsync(ct);
            try { await output.WriteLineAsync(JsonSerializer.Serialize(response, PluginProtocol.Json)); await output.FlushAsync(ct); }
            finally { writeLock.Release(); }
            if (request.Method == "shutdown") return;
        }
    }

    private static T Params<T>(RpcRequest r) =>
        r.Params is { } p ? p.Deserialize<T>(PluginProtocol.Json) ?? throw new RpcException(-32602, "Invalid params.") : throw new RpcException(-32602, "Missing params.");

    private static async Task<object> Initialize(DakshPlugin plugin, RpcRequest r, CancellationToken ct)
    {
        var p = Params<InitializeParams>(r);
        if (p.ProtocolVersion.Split('.')[0] != PluginProtocol.Version.Split('.')[0])
            throw new RpcException(-32001, $"Protocol {p.ProtocolVersion} is not supported (plugin speaks {PluginProtocol.Version}).");
        plugin.Config = p.Config;
        plugin.Secrets = p.Secrets;
        await plugin.InitializeAsync(ct);
        return new InitializeResult(plugin.Name, plugin.Version, PluginProtocol.Version);
    }

    private static async Task<object> InvokeTool(DakshPlugin plugin, RpcRequest r, CancellationToken ct)
    {
        var p = Params<ToolInvokeParams>(r);
        return await plugin.InvokeToolAsync(p.Name, p.Arguments, p.Context, ct);
    }

    private static async Task<object> InvokeHook(DakshPlugin plugin, RpcRequest r, CancellationToken ct)
    {
        var p = Params<HookInvokeParams>(r);
        return await plugin.InvokeHookAsync(p.Event, p.Payload, ct);
    }
}

public sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
