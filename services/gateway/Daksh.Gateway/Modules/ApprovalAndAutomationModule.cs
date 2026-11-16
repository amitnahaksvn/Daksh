using Daksh.Gateway.Auth;
using Daksh.Platform.Identity;

namespace Daksh.Gateway.Modules;

public sealed record ApprovalDto(Guid Id, string Title, string Risk, string Status, string Detail, DateTimeOffset ExpiresAt);
public sealed record DecideApprovalRequest(bool Approve, string? Note);
public sealed record AutomationDto(Guid Id, string Name, string Status, string? ScheduleKind, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt, int ConsecutiveFailures);
public sealed record JobExecutionDto(Guid Id, int Attempt, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);
public sealed record UsageSummaryDto(long InputTokens, long OutputTokens, decimal Cost, int Calls);

// Pending MongoDB rebuild: previously persisted approvals/automations/usage via PlatformDbContext.
public static class ApprovalAndAutomationModule
{
    public static void MapApprovalsAndAutomations(this IEndpointRouteBuilder app)
    {
        var unavailable = () => Results.Problem(statusCode: 501, title: "This feature is unavailable: pending MongoDB rebuild.");

        var a = app.MapGroup("/api/v1/approvals").RequireAuthorization().WithTags("Approvals");
        a.MapGet("/", (string? status) => unavailable()).RequirePermission(Permissions.ApprovalsRead);
        a.MapPost("/{id:guid}/decide", (Guid id, DecideApprovalRequest req, HttpContext http) => unavailable()).RequirePermission(Permissions.ApprovalsDecide);

        var au = app.MapGroup("/api/v1/automations").RequireAuthorization().WithTags("Automations");
        au.MapGet("/", () => unavailable()).RequirePermission(Permissions.AutomationsRead);
        au.MapGet("/{id:guid}/runs", (Guid id) => unavailable()).RequirePermission(Permissions.AutomationsRead);
        au.MapPost("/{id:guid}/pause", (Guid id) => unavailable()).RequirePermission(Permissions.AutomationsWrite);
        au.MapPost("/{id:guid}/resume", (Guid id) => unavailable()).RequirePermission(Permissions.AutomationsWrite);
        au.MapDelete("/{id:guid}", (Guid id) => unavailable()).RequirePermission(Permissions.AutomationsWrite);

        var u = app.MapGroup("/api/v1/usage").RequireAuthorization().WithTags("Usage");
        u.MapGet("/summary", () => unavailable()).RequirePermission(Permissions.UsageRead);
    }
}
