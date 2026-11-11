namespace Daksh.Platform.Sandbox;

public sealed record SandboxRequest(
    string Command,
    string WorkingDirectory,
    /// <summary>none | ro | rw</summary>
    string WorkspaceAccess = "rw",
    int TimeoutSeconds = 60,
    int MaxOutputBytes = 64 * 1024,
    bool Network = false,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Stdin = null,
    string? Image = null,
    /// <summary>Path inside the working directory to start in.</summary>
    string? RelativeCwd = null);

public sealed record SandboxResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Truncated, long DurationMs, string Backend)
{
    public string Render()
    {
        var parts = new List<string> { $"exit code: {ExitCode}" + (TimedOut ? " (timed out)" : "") };
        if (Stdout.Length > 0) parts.Add("stdout:\n" + Stdout);
        if (Stderr.Length > 0) parts.Add("stderr:\n" + Stderr);
        if (Truncated) parts.Add("[output was truncated]");
        return string.Join("\n", parts);
    }
}

public sealed class SandboxUnavailableException(string message) : Exception(message);

/// <summary>Runs a command outside the Gateway process. Implementations: container (the boundary) and child process (not a boundary).</summary>
public interface ISandboxRunner
{
    /// <summary>container | process</summary>
    string Mode { get; }
    /// <summary>True when this backend isolates the command from the host.</summary>
    bool IsSecurityBoundary { get; }
    /// <summary>Null when usable, otherwise the reason.</summary>
    Task<string?> UnavailableReasonAsync(CancellationToken ct);
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct);
}
