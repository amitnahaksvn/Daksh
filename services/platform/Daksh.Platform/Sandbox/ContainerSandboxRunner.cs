using System.Diagnostics;
using System.Text;
using Daksh.Platform.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Sandbox;

/// <summary>
/// Runs a command in a short-lived container: no network by default, a read-only or read-write bind mount of the
/// workspace only, and memory/CPU/pid limits. This is the real security boundary for exec and code-execution tools.
/// </summary>
public sealed class ContainerSandboxRunner(IOptions<SandboxOptions> options, ILogger<ContainerSandboxRunner> logger) : ISandboxRunner
{
    public string Mode => "container";
    public bool IsSecurityBoundary => true;

    public async Task<string?> UnavailableReasonAsync(CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(options.Value.ContainerEngine, "version --format \"{{.Server.Version}}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return $"Could not start '{options.Value.ContainerEngine}'.";
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? null : $"'{options.Value.ContainerEngine} version' exited with code {p.ExitCode}. Is the daemon running?";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"'{options.Value.ContainerEngine}' is not available: {ex.Message}";
        }
    }

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        var reason = await UnavailableReasonAsync(ct);
        if (reason is not null) throw new SandboxUnavailableException(reason);

        var sw = Stopwatch.StartNew();
        var jail = new Security.PathJail(request.WorkingDirectory);
        var mountMode = request.WorkspaceAccess == "ro" ? "ro" : "rw";
        var image = request.Image ?? options.Value.ContainerImage ?? "mcr.microsoft.com/dotnet/runtime-deps:10.0";
        var cwd = string.IsNullOrEmpty(request.RelativeCwd) ? "/workspace" : $"/workspace/{jail.Relative(jail.Resolve(request.RelativeCwd))}";

        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--network", request.Network ? "bridge" : "none",
            "--memory", options.Value.MemoryLimitBytes.ToString(),
            "--cpus", options.Value.CpuLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pids-limit", "128",
            "--security-opt", "no-new-privileges",
            "--cap-drop", "ALL",
            "--user", "65534:65534",
            "-v", $"{jail.Root}:/workspace:{mountMode}",
            "-w", cwd,
        };
        // "none" must mean no workspace access at all: remove BOTH the "-v" mount pair and the "-w" cwd pair
        // (4 elements), not just the last 2 - leaving the "-v" in place would still bind-mount the workspace
        // into the container even though the caller asked for no access to it.
        if (request.WorkspaceAccess == "none") { args.RemoveRange(args.Count - 4, 4); args.AddRange(["-w", "/tmp"]); }
        foreach (var (k, v) in request.Environment ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([image, "/bin/sh", "-c", request.Command]);

        var psi = new ProcessStartInfo(options.Value.ContainerEngine)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var maxOutput = Math.Min(request.MaxOutputBytes, options.Value.MaxOutputBytes);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var truncated = false;
        void Capture(StringBuilder sb, string? data)
        {
            if (data is null) return;
            lock (sb)
            {
                if (sb.Length >= maxOutput) { truncated = true; return; }
                var room = maxOutput - sb.Length;
                var line = data + "\n";
                sb.Append(line.Length > room ? line[..room] : line);
                if (line.Length > room) truncated = true;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data);

        logger.LogInformation("Sandbox(container) running {Image} with network={Network}", image, request.Network);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }

        return new SandboxResult(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut, truncated, sw.ElapsedMilliseconds, Mode);
    }
}

/// <summary>Picks the backend for an agent's sandbox mode, capped by the instance-wide ceiling in <see cref="SandboxOptions"/>.</summary>
public sealed class SandboxRunnerSelector(IEnumerable<ISandboxRunner> runners, IOptions<SandboxOptions> options)
{
    private readonly Dictionary<string, ISandboxRunner> _byMode = runners.ToDictionary(r => r.Mode);

    public ISandboxRunner? Resolve(string agentSandboxMode)
    {
        var instanceCeiling = options.Value.Mode;
        var effective = Rank(agentSandboxMode) < Rank(instanceCeiling) ? agentSandboxMode : instanceCeiling;
        return effective == "off" ? null : _byMode.GetValueOrDefault(effective);
    }

    private static int Rank(string mode) => mode switch { "off" => 0, "process" => 1, "container" => 2, _ => 0 };
}
