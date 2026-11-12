using Daksh.Platform.Infrastructure;
using Daksh.Platform.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Tests.Sandbox;

/// <summary>
/// <see cref="ProcessSandboxRunner"/> is documented (in its own XML doc comment and in chatgpt_changes.md) as
/// explicitly NOT a security boundary: same kernel, same user, same filesystem outside the jailed working
/// directory. These tests don't try to "fix" that - they confirm the runner behaves exactly as documented, so a
/// future change that accidentally turns it into (or advertises it as) a boundary gets caught.
/// </summary>
public sealed class ProcessSandboxRunnerTests
{
    private static ProcessSandboxRunner MakeRunner(int maxOutputBytes = 64 * 1024) =>
        new(Options.Create(new SandboxOptions { MaxOutputBytes = maxOutputBytes }), NullLogger<ProcessSandboxRunner>.Instance);

    private static string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daksh-sandbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Mode_And_IsSecurityBoundary_MatchDocumentedContract()
    {
        var runner = MakeRunner();
        Assert.Equal("process", runner.Mode);
        Assert.False(runner.IsSecurityBoundary, "The process backend must keep reporting itself as NOT a security boundary.");
    }

    [Fact]
    public async Task UnavailableReasonAsync_IsAlwaysAvailable()
    {
        // Unlike the container backend, the process backend never depends on an external daemon.
        var reason = await MakeRunner().UnavailableReasonAsync(CancellationToken.None);
        Assert.Null(reason);
    }

    [Fact]
    public async Task RunAsync_RunsARealCommandAndCapturesStdout()
    {
        var runner = MakeRunner();
        var request = new SandboxRequest("echo hello-from-process-sandbox", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-process-sandbox", result.Stdout);
        Assert.Equal("process", result.Backend);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_CapturesNonZeroExitCodeAndStderr()
    {
        var runner = MakeRunner();
        var request = new SandboxRequest("echo boom 1>&2; exit 7", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("boom", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_TimesOutLongRunningCommand()
    {
        var runner = MakeRunner();
        var request = new SandboxRequest("sleep 30", NewWorkspace(), TimeoutSeconds: 1);

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_IsNotASecurityBoundary_CommandCanReadFilesOutsideTheWorkspace()
    {
        // This is the documented trade-off, demonstrated directly: a command handed to the process backend runs
        // as a normal child process of the test, with the real filesystem, so it can read an absolute path that
        // sits entirely outside the jailed working directory. The container backend (tested separately) is the
        // one that actually prevents this.
        var outsideFile = Path.Combine(Path.GetTempPath(), "daksh-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "secret-outside-workspace");
        try
        {
            var runner = MakeRunner();
            var request = new SandboxRequest($"cat \"{outsideFile}\"", NewWorkspace());

            var result = await runner.RunAsync(request, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("secret-outside-workspace", result.Stdout);
        }
        finally { File.Delete(outsideFile); }
    }

    [Fact]
    public async Task RunAsync_ScrubsEnvironment_GatewayProcessEnvironmentDoesNotLeakThrough()
    {
        var runner = MakeRunner();
        const string marker = "DAKSH_SANDBOX_TEST_SECRET";
        Environment.SetEnvironmentVariable(marker, "should-not-leak");
        try
        {
            var shellVar = OperatingSystem.IsWindows() ? $"%{marker}%" : $"${marker}";
            var request = new SandboxRequest($"echo [{shellVar}]", NewWorkspace());

            var result = await runner.RunAsync(request, CancellationToken.None);

            // The scrubbed child environment only carries PATH/HOME/LANG plus whatever the request explicitly
            // passed, so the parent (test/Gateway) process's own environment variable must not resolve.
            Assert.DoesNotContain("should-not-leak", result.Stdout);
        }
        finally { Environment.SetEnvironmentVariable(marker, null); }
    }

    [Fact]
    public async Task RunAsync_TruncatesOutputAtMaxOutputBytes()
    {
        var runner = MakeRunner(maxOutputBytes: 16);
        var request = new SandboxRequest("echo 0123456789abcdefGHIJKLMNOP", NewWorkspace());

        var result = await runner.RunAsync(request, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.True(result.Stdout.Length <= 16);
    }

    [Fact]
    public async Task RunAsync_ThrowsSecurityViolation_WhenCwdEscapesTheWorkspace()
    {
        var runner = MakeRunner();
        var request = new SandboxRequest("echo hi", NewWorkspace(), RelativeCwd: "../../etc");

        await Assert.ThrowsAsync<Daksh.Platform.Security.SecurityViolationException>(
            () => runner.RunAsync(request, CancellationToken.None));
    }
}
