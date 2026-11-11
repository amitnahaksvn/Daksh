using Daksh.Gateway.Auth;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record ApprovalDto(Guid Id, string Title, string Risk, string Status, string Detail, DateTimeOffset ExpiresAt);
public sealed record DecideApprovalRequest(bool Approve, string? Note);
public sealed record AutomationDto(Guid Id, string Name, string Status, string? ScheduleKind, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt, int ConsecutiveFailures);
public sealed record JobExecutionDto(Guid Id, int Attempt, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);
public sealed record UsageSummaryDto(long InputTokens, long OutputTokens, decimal Cost, int Calls);

public static class ApprovalAndAutomationModule
{
    public static void MapApprovalsAndAutomations(this IEndpointRouteBuilder app)
    {
        var a = app.MapGroup("/api/v1/approvals").RequireAuthorization().WithTags("Approvals");

        a.MapGet("/", async (PlatformDbContext db, string? status, CancellationToken ct) =>
        {
            var query = db.Approvals.AsQueryable();
            query = status is null ? query.Where(x => x.Status == ApprovalStatus.Pending) : query.Where(x => x.Status.ToString() == status);
            var items = await query.OrderByDescending(x => x.CreatedAt).Take(100)
                .Select(x => new ApprovalDto(x.Id, x.Title, x.Risk, x.Status.ToString(), x.Detail, x.ExpiresAt)).ToListAsync(ct);
            return Results.Ok(items);
        }).RequirePermission(Permissions.ApprovalsRead);

        a.MapPost("/{id:guid}/decide", async (Guid id, DecideApprovalRequest req, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var approval = await db.Approvals.FindAsync([id], ct);
            if (approval is null) return Results.NotFound();
            if (approval.Status != ApprovalStatus.Pending) return Results.Problem(statusCode: 409, title: $"Approval is already {approval.Status}.");
            approval.Status = req.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            approval.DecidedByUserId = WorkspaceAndProviderModule.CurrentUserId(http);
            approval.DecidedAt = DateTimeOffset.UtcNow;
            approval.DecisionNote = req.Note;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequirePermission(Permissions.ApprovalsDecide);

        var au = app.MapGroup("/api/v1/automations").RequireAuthorization().WithTags("Automations");

        au.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Automations.Select(x => new AutomationDto(x.Id, x.Name, x.Status, x.ScheduleKind, x.NextRunAt, x.LastRunAt, x.ConsecutiveFailures)).ToListAsync(ct)))
            .RequirePermission(Permissions.AutomationsRead);

        au.MapGet("/{id:guid}/runs", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.JobExecutions.Where(x => x.AutomationId == id).OrderByDescending(x => x.StartedAt).Take(50)
                .Select(x => new JobExecutionDto(x.Id, x.Attempt, x.Status.ToString(), x.StartedAt, x.FinishedAt, x.Error)).ToListAsync(ct)))
            .RequirePermission(Permissions.AutomationsRead);

        au.MapPost("/{id:guid}/pause", async (Guid id, PlatformDbContext db, CancellationToken ct) => await SetStatus(id, "paused", db, ct))
            .RequirePermission(Permissions.AutomationsWrite);
        au.MapPost("/{id:guid}/resume", async (Guid id, PlatformDbContext db, CancellationToken ct) => await SetStatus(id, "active", db, ct))
            .RequirePermission(Permissions.AutomationsWrite);
        au.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) => await SetStatus(id, "cancelled", db, ct))
            .RequirePermission(Permissions.AutomationsWrite);

        var u = app.MapGroup("/api/v1/usage").RequireAuthorization().WithTags("Usage");
        u.MapGet("/summary", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var usage = await db.UsageRecords.GroupBy(x => 1)
                .Select(g => new { Input = g.Sum(x => x.InputTokens), Output = g.Sum(x => x.OutputTokens), Count = g.Count() })
                .FirstOrDefaultAsync(ct);
            var cost = await db.CostRecords.SumAsync(x => x.Amount, ct);
            return Results.Ok(new UsageSummaryDto(usage?.Input ?? 0, usage?.Output ?? 0, cost, usage?.Count ?? 0));
        }).RequirePermission(Permissions.UsageRead);
    }

    private static async Task<IResult> SetStatus(Guid id, string status, PlatformDbContext db, CancellationToken ct)
    {
        var automation = await db.Automations.FindAsync([id], ct);
        if (automation is null) return Results.NotFound();
        automation.Status = status;
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
