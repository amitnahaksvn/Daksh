using System.Diagnostics;
using System.Text;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Sandbox;

/// <summary>
/// Runs a command as a child process of the Gateway with a scrubbed environment, a working-directory jail and
/// resource limits. This is explicitly NOT a security boundary (same kernel, same user, same filesystem outside the
/// jailed directory): it is a convenience backend for development and for operators who accept that trade-off.
/// The container backend is the real boundary and is preferred whenever it is available.
/// </summary>
public sealed class ProcessSandboxRunner(IOptions<SandboxOptions> options, ILogger<ProcessSandboxRunner> logger) : ISandboxRunner
{
    public string Mode => "process";
    public bool IsSecurityBoundary => false;

    public Task<string?> UnavailableReasonAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var jail = new Security.PathJail(request.WorkingDirectory);
        var cwd = string.IsNullOrEmpty(request.RelativeCwd) ? jail.Root : jail.Resolve(request.RelativeCwd);
        if (!Directory.Exists(cwd)) throw new Security.SecurityViolationException("sandbox.cwd", "The working directory does not exist.");

        var (shell, shellArg) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.Stdin is not null,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(shellArg);
        psi.ArgumentList.Add(request.Command);

        // Minimal, scrubbed environment: no Gateway secrets or credentials reach the child process.
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = cwd;
        psi.EnvironmentVariables["LANG"] = "C.UTF-8";
        foreach (var (k, v) in request.Environment ?? new Dictionary<string, string>()) psi.EnvironmentVariables[k] = v;

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
                sb.Append(data.Length > room ? data[..room] : data);
                if (data.Length > room) truncated = true;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(stdout, e.Data is null ? null : e.Data + "\n");
        process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data is null ? null : e.Data + "\n");

        logger.LogInformation("Sandbox(process) running command in {Cwd}", jail.Relative(cwd));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (request.Stdin is not null)
        {
            await process.StandardInput.WriteAsync(request.Stdin);
            process.StandardInput.Close();
        }

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
