using System.Text.Json;

namespace Daksh.Platform.Tools.Core;

// Pending MongoDB rebuild: previously queried PlatformDbContext.Sessions/Messages/Automations.
public sealed class SessionsListTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("sessions_list", "Lists this agent's recent sessions.", "sessions",
        Schemas.Obj(new { limit = Schemas.Int("Maximum sessions (default 10, max 50).") }), ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Session listing is unavailable: pending MongoDB rebuild."));
}

public sealed class SessionHistoryTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("sessions_history", "Reads recent messages from another of this agent's sessions.", "sessions",
        Schemas.Obj(new { session_id = Schemas.Str("Session id."), limit = Schemas.Int("Maximum messages (default 20, max 100).") }, ["session_id"]),
        ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true, ReturnsUntrustedContent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Session history is unavailable: pending MongoDB rebuild."));
}

public sealed class ScheduleCreateTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("schedule_create", "Creates a scheduled or recurring task that will prompt this agent later.", "automation",
        Schemas.Obj(new
        {
            name = Schemas.Str("Short name for the schedule."),
            kind = new { type = "string", @enum = new[] { "at", "every", "cron" } },
            in_seconds = Schemas.Int("For kind=at: run this many seconds from now."),
            every_seconds = Schemas.Int("For kind=every: repeat every this many seconds (minimum 5)."),
            cron = Schemas.Str("For kind=cron: a 5-field cron expression."),
            timezone = Schemas.Str("IANA time zone for cron (default UTC)."),
            prompt = Schemas.Str("What to ask the agent to do when it runs."),
        }, ["name", "kind", "prompt"]), ToolRisk.Medium, TimeoutSeconds: 10);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Schedule creation is unavailable: pending MongoDB rebuild."));
}

public sealed class ScheduleListTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("schedule_list", "Lists this agent's active schedules.", "automation",
        Schemas.Obj(new { }), ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Schedule listing is unavailable: pending MongoDB rebuild."));
}

public sealed class ScheduleCancelTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("schedule_cancel", "Cancels a schedule created by this agent.", "automation",
        Schemas.Obj(new { schedule_id = Schemas.Str("Schedule id to cancel.") }, ["schedule_id"]), ToolRisk.Medium, TimeoutSeconds: 10);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Schedule cancellation is unavailable: pending MongoDB rebuild."));
}
