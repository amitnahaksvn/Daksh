using Daksh.Gateway.Auth;
using Daksh.Platform.Identity;

namespace Daksh.Gateway.Modules;

public sealed record CreateSessionRequest(Guid AgentId, string? Title);
public sealed record SessionDto(Guid Id, Guid AgentId, string Title, string Status, DateTimeOffset? LastMessageAt);
public sealed record SendMessageRequest(string Text);
public sealed record MessageDto(Guid Id, long Seq, string Role, string Content, string? ToolName, DateTimeOffset CreatedAt);
public sealed record ExecutionDto(Guid Id, string Status, string? FinalText, string? Error, int Step, long InputTokens, long OutputTokens, decimal Cost);

// Pending MongoDB rebuild: previously persisted sessions/messages/executions via PlatformDbContext.
public static class SessionModule
{
    public static void MapSessions(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/sessions").RequireAuthorization().WithTags("Sessions");
        var unavailable = () => Results.Problem(statusCode: 501, title: "Sessions are unavailable: pending MongoDB rebuild.");

        g.MapPost("/", (CreateSessionRequest req, HttpContext http) => unavailable()).RequirePermission(Permissions.SessionsWrite);
        g.MapGet("/", (HttpContext http) => unavailable()).RequirePermission(Permissions.SessionsRead);
        g.MapGet("/{id:guid}/messages", (Guid id, long? after) => unavailable()).RequirePermission(Permissions.SessionsRead);
        g.MapPost("/{id:guid}/messages", (Guid id, SendMessageRequest req, HttpContext http) => unavailable()).RequirePermission(Permissions.ChatSend);
        g.MapGet("/{id:guid}/executions/{executionId:guid}", (Guid id, Guid executionId) => unavailable()).RequirePermission(Permissions.SessionsRead);
        g.MapPost("/{id:guid}/executions/{executionId:guid}/cancel", (Guid id, Guid executionId) => unavailable()).RequirePermission(Permissions.ChatSend);
        g.MapDelete("/{id:guid}", (Guid id) => unavailable()).RequirePermission(Permissions.SessionsWrite);
    }
}
