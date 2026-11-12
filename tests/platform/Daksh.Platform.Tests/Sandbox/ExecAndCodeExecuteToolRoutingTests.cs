using System.Text.Json;
using Daksh.Platform.Sandbox;
using Daksh.Platform.Security;
using Daksh.Platform.Tools;
using Daksh.Platform.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Daksh.Platform.Tests.Sandbox;

/// <summary>
/// Confirms that <see cref="ExecTool"/> and <see cref="CodeExecuteTool"/> never run a command themselves: every
/// path goes through <see cref="SandboxRunnerSelector"/>, and a command that the <see cref="CommandPolicy"/>
/// refuses never reaches a runner at all. This is the "exec only via sandbox" part of Phase 5 - it uses fake
/// <see cref="ISandboxRunner"/> implementations (via NSubstitute) rather than a real process or container, so it
/// tests the routing/wiring, not the backends themselves (those are covered by
/// <see cref="ProcessSandboxRunnerTests"/> and <see cref="ContainerSandboxRunnerTests"/>).
/// </summary>
public sealed class ExecAndCodeExecuteToolRoutingTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static (ToolContext Context, ISandboxRunner ProcessRunner, ISandboxRunner ContainerRunner, IServiceProvider Services)
        BuildContext(string sandboxMode, bool registerProcessRunner = true, bool registerContainerRunner = true, string workspaceAccess = "rw")
    {
        var processRunner = Substitute.For<ISandboxRunner>();
        processRunner.Mode.Returns("process");
        var containerRunner = Substitute.For<ISandboxRunner>();
        containerRunner.Mode.Returns("container");

        var services = new ServiceCollection();
        services.AddSingleton(new CommandPolicy(new CommandPolicyOptions()));
        var runners = new List<ISandboxRunner>();
        if (registerProcessRunner) runners.Add(processRunner);
        if (registerContainerRunner) runners.Add(containerRunner);
        services.AddSingleton<IEnumerable<ISandboxRunner>>(runners);
        services.AddSingleton(sp => new SandboxRunnerSelector(sp.GetRequiredService<IEnumerable<ISandboxRunner>>(),
            Microsoft.Extensions.Options.Options.Create(new Daksh.Platform.Infrastructure.SandboxOptions { Mode = "container" })));
        var provider = services.BuildServiceProvider();

        var workspaceDir = Path.Combine(Path.GetTempPath(), "daksh-exectool-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        var context = new ToolContext
        {
            WorkspaceId = Guid.NewGuid(), AgentId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(), RootExecutionId = Guid.NewGuid(), ToolCallId = "call_1",
            WorkspaceDirectory = workspaceDir, WorkspaceAccess = workspaceAccess, SandboxMode = sandboxMode, Services = provider,
        };
        return (context, processRunner, containerRunner, provider);
    }

    // ---- ExecTool ----

    [Fact]
    public async Task ExecTool_SandboxModeOff_FailsWithoutConsultingAnyRunner()
    {
        var (context, processRunner, containerRunner, _) = BuildContext("off");
        var tool = new ExecTool();

        var result = await tool.ExecuteAsync(context, Args(new { command = "echo hi" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("disabled", result.Content, StringComparison.OrdinalIgnoreCase);
        await processRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
        await containerRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecTool_CommandPolicyRefuses_NeverReachesTheSandboxRunner()
    {
        var (context, processRunner, containerRunner, _) = BuildContext("process");
        var tool = new ExecTool();

        // A null byte is refused unconditionally by CommandPolicy.Evaluate, independent of any configured deny list.
        var result = await tool.ExecuteAsync(context, Args(new { command = "echo hi\0" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("refused", result.Content, StringComparison.OrdinalIgnoreCase);
        await processRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
        await containerRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecTool_NoRunnerAvailableForMode_Fails()
    {
        var (context, processRunner, containerRunner, _) = BuildContext("process", registerProcessRunner: false);
        var tool = new ExecTool();

        var result = await tool.ExecuteAsync(context, Args(new { command = "echo hi" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("No sandbox backend is available", result.Content);
        await containerRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecTool_RunsThroughTheResolvedRunner_WithNoNetworkAndTheGivenCommand()
    {
        var (context, processRunner, _, _) = BuildContext("process");
        processRunner.RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SandboxResult(0, "ok\n", "", false, false, 5, "process"));
        var tool = new ExecTool();

        var result = await tool.ExecuteAsync(context, Args(new { command = "echo hi" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("ok", result.Content);
        await processRunner.Received(1).RunAsync(
            Arg.Is<SandboxRequest>(r => r.Command == "echo hi" && r.Network == false && r.WorkingDirectory == context.WorkspaceDirectory),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecTool_SandboxUnavailableException_IsReportedAsFailureNotThrown()
    {
        var (context, processRunner, _, _) = BuildContext("process");
        processRunner.RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SandboxResult>(new SandboxUnavailableException("daemon down")));
        var tool = new ExecTool();

        var result = await tool.ExecuteAsync(context, Args(new { command = "echo hi" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Sandbox unavailable", result.Content);
    }

    // ---- CodeExecuteTool ----

    [Fact]
    public async Task CodeExecuteTool_NonContainerSandboxMode_RefusesWithoutConsultingAnyRunner()
    {
        var (context, processRunner, containerRunner, _) = BuildContext("process");
        var tool = new CodeExecuteTool();

        var result = await tool.ExecuteAsync(context, Args(new { language = "python", code = "print(1)" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("container sandbox", result.Content, StringComparison.OrdinalIgnoreCase);
        await processRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
        await containerRunner.DidNotReceive().RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CodeExecuteTool_ContainerModeButNoContainerRunnerRegistered_Fails()
    {
        var (context, _, containerRunner, _) = BuildContext("container", registerContainerRunner: false);
        var tool = new CodeExecuteTool();

        var result = await tool.ExecuteAsync(context, Args(new { language = "python", code = "print(1)" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not available", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodeExecuteTool_ContainerMode_WritesScriptUnderWorkspaceAndRunsItWithNoNetwork()
    {
        var (context, _, containerRunner, _) = BuildContext("container");
        containerRunner.RunAsync(Arg.Any<SandboxRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SandboxResult(0, "1\n", "", false, false, 5, "container"));
        var tool = new CodeExecuteTool();

        var result = await tool.ExecuteAsync(context, Args(new { language = "python", code = "print(1)" }), CancellationToken.None);

        Assert.True(result.Ok);
        await containerRunner.Received(1).RunAsync(
            Arg.Is<SandboxRequest>(r => r.Command.StartsWith("python3 /workspace/.scripts/run_") && r.Network == false),
            Arg.Any<CancellationToken>());
        var scriptsDir = Path.Combine(context.WorkspaceDirectory, ".scripts");
        Assert.True(Directory.Exists(scriptsDir));
        Assert.Single(Directory.GetFiles(scriptsDir, "*.py"));
    }
}
