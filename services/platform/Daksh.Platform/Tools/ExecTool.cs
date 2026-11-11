using Daksh.Platform.Infrastructure;
using Daksh.Platform.Sandbox;
using Daksh.Platform.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Tools.Core;

public sealed class ExecTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("exec", "Runs a shell command in a sandboxed workspace and returns its output.", "runtime",
        Schemas.Obj(new
        {
            command = Schemas.Str("The shell command to run."),
            timeout_seconds = Schemas.Int("Maximum seconds to wait (default 60, max 300)."),
        }, ["command"]), ToolRisk.High, TimeoutSeconds: 300, MaxRetries: 0, ParallelSafe: false, ReturnsUntrustedContent: true);

    public ToolRisk AssessRisk(System.Text.Json.JsonElement arguments)
    {
        var command = arguments.Str("command") ?? "";
        return command.Contains("rm ") || command.Contains("curl") || command.Contains("wget") ? ToolRisk.Critical : ToolRisk.High;
    }

    public string? Unavailable(IServiceProvider services) => null;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.SandboxMode == "off") return ToolResult.Fail("Shell execution is disabled for this agent (sandbox mode is 'off').");
        var command = arguments.Required("command");
        var policy = context.Services.GetRequiredService<CommandPolicy>();
        var verdict = policy.Evaluate(command);
        if (!verdict.Allowed) return ToolResult.Fail($"Command refused: {verdict.Reason}");

        var selector = context.Services.GetRequiredService<SandboxRunnerSelector>();
        var runner = selector.Resolve(context.SandboxMode);
        if (runner is null) return ToolResult.Fail("No sandbox backend is available to run this command.");

        var timeout = Math.Clamp(arguments.Int("timeout_seconds") ?? 60, 1, 300);
        try
        {
            var result = await runner.RunAsync(new SandboxRequest(command, context.WorkspaceDirectory, context.WorkspaceAccess, timeout, Network: false), ct);
            return ToolResult.Success(result.Render());
        }
        catch (SandboxUnavailableException ex) { return ToolResult.Fail($"Sandbox unavailable: {ex.Message}"); }
        catch (SecurityViolationException ex) { return ToolResult.Fail($"Security policy refused this command: {ex.Message}"); }
    }
}

public sealed class CodeExecuteTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("code_execute", "Runs a short Python or Node.js script in the container sandbox only.", "runtime",
        Schemas.Obj(new
        {
            language = new { type = "string", @enum = new[] { "python", "node" } },
            code = Schemas.Str("The script source."),
        }, ["language", "code"]), ToolRisk.High, TimeoutSeconds: 120, ParallelSafe: false, ReturnsUntrustedContent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.SandboxMode != "container")
            return Task.FromResult(ToolResult.Fail("code_execute requires the container sandbox (agent sandbox mode must be 'container')."));
        var selector = context.Services.GetRequiredService<SandboxRunnerSelector>();
        var runner = selector.Resolve("container");
        if (runner is null) return Task.FromResult(ToolResult.Fail("The container sandbox is not available."));
        var language = arguments.Required("language");
        var code = arguments.Required("code");
        var tmp = Path.Combine(context.WorkspaceDirectory, ".scripts");
        Directory.CreateDirectory(tmp);
        var ext = language == "python" ? "py" : "js";
        var name = $".scripts/run_{Guid.NewGuid():N}.{ext}";
        File.WriteAllText(Path.Combine(context.WorkspaceDirectory, name), code);
        var command = language == "python" ? $"python3 /workspace/{name}" : $"node /workspace/{name}";
        return RunAsync(runner, command, context, ct);
    }

    private static async Task<ToolResult> RunAsync(ISandboxRunner runner, string command, ToolContext context, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(new SandboxRequest(command, context.WorkspaceDirectory, context.WorkspaceAccess, 120, Network: false), ct);
            return ToolResult.Success(result.Render());
        }
        catch (SandboxUnavailableException ex) { return ToolResult.Fail($"Sandbox unavailable: {ex.Message}"); }
    }
}
