using Daksh.Gateway.Auth;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Daksh.Platform.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record CreateSessionRequest(Guid AgentId, string? Title);
public sealed record SessionDto(Guid Id, Guid AgentId, string Title, string Status, DateTimeOffset? LastMessageAt);
public sealed record SendMessageRequest(string Text);
public sealed record MessageDto(Guid Id, long Seq, string Role, string Content, string? ToolName, DateTimeOffset CreatedAt);
public sealed record ExecutionDto(Guid Id, string Status, string? FinalText, string? Error, int Step, long InputTokens, long OutputTokens, decimal Cost);

public static class SessionModule
{
    public static void MapSessions(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/sessions").RequireAuthorization().WithTags("Sessions");

        g.MapPost("/", async (CreateSessionRequest req, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var agent = await db.Agents.FindAsync([req.AgentId], ct);
            if (agent is null || !agent.Enabled) return Results.Problem(statusCode: 400, title: "Agent not found or disabled.");
            var session = new Session
            {
                AgentId = req.AgentId, OwnerUserId = WorkspaceAndProviderModule.CurrentUserId(http),
                Title = req.Title ?? $"Chat with {agent.Name}", Kind = SessionKind.Direct,
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(session));
        }).RequirePermission(Permissions.SessionsWrite);

        g.MapGet("/", async (PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var userId = WorkspaceAndProviderModule.CurrentUserId(http);
            var canReadAll = http.RequestServices.GetRequiredService<TenantContext>().Has(Permissions.SessionsReadAll);
            var query = canReadAll ? db.Sessions.AsQueryable() : db.Sessions.Where(s => s.OwnerUserId == userId || s.Shared);
            var items = await query.OrderByDescending(s => s.LastMessageAt).Take(50).ToListAsync(ct);
            return Results.Ok(items.Select(ToDto));
        }).RequirePermission(Permissions.SessionsRead);

        g.MapGet("/{id:guid}/messages", async (Guid id, PlatformDbContext db, long? after, CancellationToken ct) =>
        {
            var session = await db.Sessions.FindAsync([id], ct);
            if (session is null) return Results.NotFound();
            var items = await db.Messages.Where(m => m.SessionId == id && m.Seq > (after ?? 0)).OrderBy(m => m.Seq)
                .Select(m => new MessageDto(m.Id, m.Seq, m.Role, m.Content, m.ToolName, m.CreatedAt)).ToListAsync(ct);
            return Results.Ok(items);
        }).RequirePermission(Permissions.SessionsRead);

        g.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest req, PlatformDbContext db,
            ExecutionQueueService queue, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "Text is required.");
            var session = await db.Sessions.FindAsync([id], ct);
            if (session is null) return Results.NotFound();
            var agent = await db.Agents.FindAsync([session.AgentId], ct);
            if (agent is null || !agent.Enabled) return Results.Problem(statusCode: 400, title: "Agent not found or disabled.");

            var message = new Message { SessionId = id, Seq = session.NextSeq++, Role = "user", Content = req.Text, Epoch = session.Epoch, AuthorUserId = WorkspaceAndProviderModule.CurrentUserId(http) };
            db.Messages.Add(message);
            session.LastMessageAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var hasActive = await queue.HasActiveRunAsync(id, ct);
            if (hasActive) return Results.Accepted(value: new { queued = true });

            var execution = await queue.StartAsync(session, agent, message.Seq, "chat", WorkspaceAndProviderModule.CurrentUserId(http), null, 0, null, null, null, ct);
            return Results.Accepted($"/api/v1/sessions/{id}/executions/{execution.Id}", new { executionId = execution.Id });
        }).RequirePermission(Permissions.ChatSend);

        g.MapGet("/{id:guid}/executions/{executionId:guid}", async (Guid id, Guid executionId, PlatformDbContext db, CancellationToken ct) =>
        {
            var e = await db.Executions.FirstOrDefaultAsync(x => x.Id == executionId && x.SessionId == id, ct);
            return e is null ? Results.NotFound() : Results.Ok(new ExecutionDto(e.Id, e.Status.ToString(), e.FinalText, e.Error, e.Step, e.InputTokens, e.OutputTokens, e.Cost));
        }).RequirePermission(Permissions.SessionsRead);

        g.MapPost("/{id:guid}/executions/{executionId:guid}/cancel", async (Guid id, Guid executionId, ExecutionQueueService queue, CancellationToken ct) =>
        {
            await queue.RequestCancelAsync(executionId, ct);
            return Results.Ok();
        }).RequirePermission(Permissions.ChatSend);

        g.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var session = await db.Sessions.FindAsync([id], ct);
            if (session is null) return Results.NotFound();
            session.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SessionsWrite);
    }

    private static SessionDto ToDto(Session s) => new(s.Id, s.AgentId, s.Title, s.Status.ToString(), s.LastMessageAt);
}
