using Daksh.Platform.Infrastructure;
using Daksh.Platform.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Tests.Sandbox;

/// <summary>
/// <see cref="ContainerSandboxRunner"/> is the real security boundary: no network, dropped capabilities, and
/// resource limits. These tests actually invoke the Docker daemon (skipped automatically if one isn't
/// reachable, e.g. in a CI image without Docker) rather than only inspecting argument construction, because the
/// behaviour that matters - no network reaching the container, a non-root unprivileged user, resource limits
/// actually being passed to `docker run` - can only be proven by actually running a container.
/// </summary>
public sealed class ContainerSandboxRunnerTests
{
    private const string TestImage = "alpine:3.19";

    private static ContainerSandboxRunner MakeRunner(long memoryLimitBytes = 128 * 1024 * 1024, double cpuLimit = 0.5) =>
        new(Options.Create(new SandboxOptions
        {
            ContainerEngine = "docker",
            ContainerImage = TestImage,
            MemoryLimitBytes = memoryLimitBytes,
            CpuLimit = cpuLimit,
            MaxOutputBytes = 64 * 1024,
        }), NullLogger<ContainerSandboxRunner>.Instance);

    private static string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daksh-container-sandbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Skips the test body (reporting it as passed) when no Docker daemon is reachable, instead of
    /// failing a whole CI run that simply doesn't have Docker available.</summary>
    private static async Task<bool> DockerAvailableAsync()
    {
        var reason = await MakeRunner().UnavailableReasonAsync(CancellationToken.None);
        return reason is null;
    }

    [Fact]
    public void Mode_And_IsSecurityBoundary_MatchDocumentedContract()
    {
        var runner = MakeRunner();
        Assert.Equal("container", runner.Mode);
        Assert.True(runner.IsSecurityBoundary, "The container backend is documented as the real security boundary.");
    }

    [Fact]
    public async Task RunAsync_RunsARealContainerAndCapturesStdout()
    {
        if (!await DockerAvailableAsync()) return; // no Docker daemon reachable in this environment
        var runner = MakeRunner();
        var request = new SandboxRequest("echo hello-from-container-sandbox", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-container-sandbox", result.Stdout);
        Assert.Equal("container", result.Backend);
    }

    [Fact]
    public async Task RunAsync_DefaultRequest_HasNoNetworkAccess()
    {
        // SandboxRequest.Network defaults to false, which ContainerSandboxRunner must turn into "--network none".
        // Prove it behaviourally: a container that cannot reach the network must fail to connect, not merely be
        // slow. BusyBox/alpine's wget is used because it's already in the test image.
        if (!await DockerAvailableAsync()) return;
        var runner = MakeRunner();
        var request = new SandboxRequest("wget -T 3 -q -O /dev/null http://169.254.169.254/ || echo NO_NETWORK", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Contains("NO_NETWORK", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_ExplicitNetworkTrue_CanReachTheRealNetwork()
    {
        // Contrast case for the previous test: when a caller explicitly asks for network access, the container
        // must actually get it (otherwise the no-network test above would be meaningless - it could just be a
        // broken image, not real isolation). Resolves a well-known public host, requiring an outbound
        // connection to succeed.
        if (!await DockerAvailableAsync()) return;
        var runner = MakeRunner();
        var request = new SandboxRequest("wget -T 5 -q -O /dev/null https://example.com/ && echo HAS_NETWORK", NewWorkspace(), Network: true);

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Contains("HAS_NETWORK", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_RunsAsUnprivilegedNonRootUser()
    {
        // --user 65534:65534 (nobody/nogroup) plus --cap-drop ALL: confirm the effective uid inside the
        // container is not 0, so a compromised tool call inside the container is not root even in there.
        if (!await DockerAvailableAsync()) return;
        var runner = MakeRunner();
        var request = new SandboxRequest("id -u", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("65534", result.Stdout.Trim());
    }

    [Fact]
    public async Task RunAsync_WorkspaceAccessNone_DoesNotMountTheWorkspace()
    {
        if (!await DockerAvailableAsync()) return;
        var workspace = NewWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace, "secret.txt"), "should-not-be-visible");
        var runner = MakeRunner();
        var request = new SandboxRequest("ls /workspace 2>&1 || echo NOT_MOUNTED", workspace, WorkspaceAccess: "none");

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.DoesNotContain("secret.txt", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_WorkspaceAccessRo_MountsReadOnly()
    {
        if (!await DockerAvailableAsync()) return;
        var runner = MakeRunner();
        var request = new SandboxRequest("touch /workspace/should-fail.txt 2>&1 || echo READ_ONLY", NewWorkspace(), WorkspaceAccess: "ro");

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Contains("READ_ONLY", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_MemoryLimit_IsEnforcedByTheContainerRuntime()
    {
        // Ask for far more memory than the configured limit allows; the OOM killer inside the container must
        // kill the process before it can report success, proving --memory was actually passed to `docker run`
        // and not merely stored in options.
        if (!await DockerAvailableAsync()) return;
        var runner = MakeRunner(memoryLimitBytes: 32 * 1024 * 1024);
        // Hold ~200MB resident in a shell variable, far beyond the 32MB cgroup limit: the kernel OOM-killer
        // must kill the process (exit 137) before "done" is reached, proving --memory actually reached
        // `docker run` rather than only being stored in options.
        var request = new SandboxRequest(
            "a=$(yes | tr -d '\\n' | head -c 200000000); echo done", NewWorkspace(), WorkspaceAccess: "none", TimeoutSeconds: 20);

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.False(result.ExitCode == 0 && result.Stdout.Contains("done"),
            $"Expected the 32MB memory limit to prevent a clean 200MB in-process allocation. stdout={result.Stdout} stderr={result.Stderr} exit={result.ExitCode}");
    }

    [Fact]
    public async Task UnavailableReasonAsync_ReportsNullWhenDockerIsReachable()
    {
        if (!await DockerAvailableAsync()) return;
        var reason = await MakeRunner().UnavailableReasonAsync(CancellationToken.None);
        Assert.Null(reason);
    }

    [Fact]
    public async Task UnavailableReasonAsync_ReportsAReason_WhenTheEngineBinaryDoesNotExist()
    {
        var runner = new ContainerSandboxRunner(Options.Create(new SandboxOptions { ContainerEngine = "daksh-no-such-engine-binary" }),
            NullLogger<ContainerSandboxRunner>.Instance);

        var reason = await runner.UnavailableReasonAsync(CancellationToken.None);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task RunAsync_ThrowsSandboxUnavailable_WhenTheEngineIsMissing()
    {
        var runner = new ContainerSandboxRunner(Options.Create(new SandboxOptions { ContainerEngine = "daksh-no-such-engine-binary" }),
            NullLogger<ContainerSandboxRunner>.Instance);
        var request = new SandboxRequest("echo hi", NewWorkspace());

        await Assert.ThrowsAsync<SandboxUnavailableException>(() => runner.RunAsync(request, CancellationToken.None));
    }
}
