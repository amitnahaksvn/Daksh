using Daksh.Platform.Infrastructure;
using Daksh.Platform.Sandbox;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Daksh.Platform.Tests.Sandbox;

/// <summary>
/// <see cref="SandboxRunnerSelector"/> is the only place that decides which backend an agent's sandbox mode
/// actually runs under. These tests cover the capping rule described in <see cref="SandboxOptions.Mode"/>:
/// the per-agent setting is capped by the instance-wide ceiling, never the other way around.
/// </summary>
public sealed class SandboxRunnerSelectorTests
{
    private static ISandboxRunner FakeRunner(string mode)
    {
        var runner = Substitute.For<ISandboxRunner>();
        runner.Mode.Returns(mode);
        return runner;
    }

    private static SandboxRunnerSelector Selector(string instanceCeiling, params ISandboxRunner[] runners) =>
        new(runners, Options.Create(new SandboxOptions { Mode = instanceCeiling }));

    [Fact]
    public void Resolve_AgentBelowCeiling_UsesAgentMode()
    {
        var process = FakeRunner("process");
        var container = FakeRunner("container");
        var selector = Selector("container", process, container);

        var resolved = selector.Resolve("process");

        Assert.Same(process, resolved);
    }

    [Fact]
    public void Resolve_AgentAboveCeiling_IsCappedDownToCeiling()
    {
        // The agent asks for "container" but the instance-wide ceiling only allows "process": the agent must
        // not get a stronger backend than the operator configured for the whole instance.
        var process = FakeRunner("process");
        var container = FakeRunner("container");
        var selector = Selector("process", process, container);

        var resolved = selector.Resolve("container");

        Assert.Same(process, resolved);
        Assert.NotSame(container, resolved);
    }

    [Fact]
    public void Resolve_CeilingOff_DisablesSandboxRegardlessOfAgentSetting()
    {
        var process = FakeRunner("process");
        var container = FakeRunner("container");
        var selector = Selector("off", process, container);

        Assert.Null(selector.Resolve("container"));
        Assert.Null(selector.Resolve("process"));
    }

    [Fact]
    public void Resolve_AgentOff_IsHonoredEvenWhenCeilingAllowsMore()
    {
        // An agent may always ask for LESS than the ceiling; "off" must disable the sandbox for that agent even
        // though the instance permits "container".
        var process = FakeRunner("process");
        var container = FakeRunner("container");
        var selector = Selector("container", process, container);

        Assert.Null(selector.Resolve("off"));
    }

    [Fact]
    public void Resolve_SameMode_UsesThatRunner()
    {
        var container = FakeRunner("container");
        var selector = Selector("container", container);

        Assert.Same(container, selector.Resolve("container"));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    public void Resolve_UnknownAgentMode_RanksAsOff(string unknownMode)
    {
        // Rank() maps anything it doesn't recognise to the "off" rank (0), so an invalid/unset per-agent value
        // never accidentally grants a stronger-than-off backend.
        var process = FakeRunner("process");
        var container = FakeRunner("container");
        var selector = Selector("container", process, container);

        Assert.Null(selector.Resolve(unknownMode));
    }

    [Fact]
    public void Resolve_CeilingHigherThanAnyRegisteredRunner_StillCapsAgentToItsOwnMode()
    {
        // Only a process runner is registered, but the ceiling is "container": an agent asking for "process"
        // should still resolve normally (it is below the ceiling), independent of what else is registered.
        var process = FakeRunner("process");
        var selector = Selector("container", process);

        Assert.Same(process, selector.Resolve("process"));
    }
}
