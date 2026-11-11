namespace Daksh.Platform.Infrastructure;

public sealed class StorageOptions
{
    /// <summary>Root directory for stored files, skill catalogs and sandbox workspaces.</summary>
    public string Root { get; set; } = "./data";
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;
}

public sealed class SandboxOptions
{
    /// <summary>off | process | container. Per-agent setting is capped by this instance-wide ceiling.</summary>
    public string Mode { get; set; } = "process";
    public string? ContainerImage { get; set; } = "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    public string ContainerEngine { get; set; } = "docker";
    public long MemoryLimitBytes { get; set; } = 512 * 1024 * 1024;
    public double CpuLimit { get; set; } = 1.0;
    public int MaxOutputBytes { get; set; } = 64 * 1024;
    public List<string> AllowPrograms { get; set; } = [];
}

public sealed class RuntimeOptions
{
    public int DefaultRunTimeoutSeconds { get; set; } = 900;
    public int LeaseSeconds { get; set; } = 120;
    public int PollIntervalMs { get; set; } = 500;
    public int WorkerConcurrency { get; set; } = 4;
}

public sealed class SchedulerOptions
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 20;
}

public sealed class SecurityOptions
{
    public Security.SsrfOptions Ssrf { get; set; } = new();
    public Security.CommandPolicyOptions Command { get; set; } = new();
}
