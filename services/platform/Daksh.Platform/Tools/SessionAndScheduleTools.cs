using System.Text.Json;
using Daksh.Platform.Automation;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Platform.Tools.Core;

public sealed class SessionsListTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("sessions_list", "Lists this agent's recent sessions.", "sessions",
        Schemas.Obj(new { limit = Schemas.Int("Maximum sessions (default 10, max 50).") }), ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var limit = Math.Clamp(arguments.Int("limit") ?? 10, 1, 50);
        var sessions = await db.Sessions.Where(s => s.AgentId == context.AgentId).OrderByDescending(s => s.LastMessageAt)
            .Take(limit).Select(s => new { s.Id, s.Title, s.Kind, s.LastMessageAt }).ToListAsync(ct);
        return ToolResult.Json(sessions);
    }
}

public sealed class SessionHistoryTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("sessions_history", "Reads recent messages from another of this agent's sessions.", "sessions",
        Schemas.Obj(new { session_id = Schemas.Str("Session id."), limit = Schemas.Int("Maximum messages (default 20, max 100).") }, ["session_id"]),
        ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true, ReturnsUntrustedContent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        if (!Guid.TryParse(arguments.Required("session_id"), out var sid)) return ToolResult.Fail("session_id is not a valid id.");
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sid && s.AgentId == context.AgentId, ct);
        if (session is null) return ToolResult.Fail("Session not found.");
        var limit = Math.Clamp(arguments.Int("limit") ?? 20, 1, 100);
        var messages = await db.Messages.Where(m => m.SessionId == sid).OrderByDescending(m => m.Seq).Take(limit)
            .OrderBy(m => m.Seq).Select(m => $"[{m.Role}] {m.Content}").ToListAsync(ct);
        return ToolResult.Success(Security.Untrusted.Wrap($"session:{sid}", string.Join("\n", messages)));
    }
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

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var scheduler = context.Services.GetRequiredService<SchedulerService>();
        var kind = arguments.Required("kind");
        var spec = new ScheduleSpec(kind, arguments.Str("cron"), arguments.Str("timezone") ?? "UTC",
            arguments.Int("every_seconds"), kind == "at" ? DateTimeOffset.UtcNow.AddSeconds(arguments.Int("in_seconds") ?? 3600) : null);
        var errors = ScheduleCalculator.Validate(spec, DateTimeOffset.UtcNow);
        if (errors.Count > 0) return ToolResult.Fail("Invalid schedule: " + string.Join(" ", errors));

        var automation = new Domain.Automation
        {
            WorkspaceId = context.WorkspaceId, OwnerUserId = context.UserId ?? Guid.Empty, AgentId = context.AgentId,
            Name = arguments.Required("name"), TriggerKind = "schedule", ScheduleKind = kind, Cron = spec.Cron,
            TimeZone = spec.TimeZone, EverySeconds = spec.EverySeconds, RunAt = spec.RunAt, ActionKind = "agent_prompt",
            Action = JsonSerializer.Serialize(new { prompt = arguments.Required("prompt") }),
            Delivery = JsonSerializer.Serialize(new { kind = "session", session_id = context.SessionId }),
            SessionId = context.SessionId,
        };
        db.Automations.Add(automation);
        await db.SaveChangesAsync(ct);
        await scheduler.ScheduleNextAsync(automation, ct);
        return ToolResult.Success($"Scheduled '{automation.Name}' (id {automation.Id}), next run {automation.NextRunAt:u}.");
    }
}

public sealed class ScheduleListTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("schedule_list", "Lists this agent's active schedules.", "automation",
        Schemas.Obj(new { }), ToolRisk.Low, TimeoutSeconds: 10, Idempotent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var items = await db.Automations.Where(a => a.AgentId == context.AgentId && a.Status == "active")
            .Select(a => new { a.Id, a.Name, a.ScheduleKind, a.NextRunAt }).ToListAsync(ct);
        return ToolResult.Json(items);
    }
}

public sealed class ScheduleCancelTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("schedule_cancel", "Cancels a schedule created by this agent.", "automation",
        Schemas.Obj(new { schedule_id = Schemas.Str("Schedule id to cancel.") }, ["schedule_id"]), ToolRisk.Medium, TimeoutSeconds: 10);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        if (!Guid.TryParse(arguments.Required("schedule_id"), out var id)) return ToolResult.Fail("schedule_id is not a valid id.");
        var automation = await db.Automations.FirstOrDefaultAsync(a => a.Id == id && a.AgentId == context.AgentId, ct);
        if (automation is null) return ToolResult.Fail("Schedule not found.");
        automation.Status = "cancelled";
        await db.SaveChangesAsync(ct);
        return ToolResult.Success("Cancelled.");
    }
}
