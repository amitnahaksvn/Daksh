using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using FluentValidation;

namespace Daksh.Api.Modules;

public sealed record SendMessageRequest(string? Message, Guid? ConversationId);
public sealed record AssistantReply(Guid ConversationId, Guid MessageId, string Reply, string Provider, bool IsSimulated);
public sealed record ConversationSummary(Guid Id, DateTimeOffset CreatedAt, int MessageCount);
public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);

public sealed class SendMessageValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageValidator() =>
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
}

// Pending MongoDB rebuild: previously persisted conversations/messages via DakshDbContext.
public static class AssistantModule
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/assistant").RequireAuthorization().WithTags("Assistant");

        g.MapPost("/messages", (SendMessageRequest req, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "The assistant is unavailable: pending MongoDB rebuild.")).Validate<SendMessageRequest>();

        g.MapGet("/conversations", (HttpContext http) =>
            Results.Problem(statusCode: 501, title: "The assistant is unavailable: pending MongoDB rebuild."));

        g.MapGet("/conversations/{id:guid}/messages", (Guid id, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "The assistant is unavailable: pending MongoDB rebuild."));
    }
}
