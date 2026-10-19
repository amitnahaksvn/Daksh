using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using Daksh.Domain;
using Daksh.Ai;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

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

public static class AssistantModule
{
    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/assistant").RequireAuthorization().WithTags("Assistant");

        g.MapPost("/messages", async (SendMessageRequest req, HttpContext http, DakshDbContext db,
                IAssistantOrchestrator assistant, IAuditLog audit, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            Conversation convo;
            if (req.ConversationId is { } cid)
            {
                convo = await db.Conversations.Include(c => c.Messages)
                            .FirstOrDefaultAsync(c => c.Id == cid && c.UserId == userId, ct)
                        ?? throw new KeyNotFoundException();
            }
            else
            {
                convo = new Conversation { UserId = userId };
                db.Conversations.Add(convo);
            }

            var history = convo.Messages.OrderBy(m => m.CreatedAt).Select(m => (m.Role, m.Content)).ToList();
            var userMsg = new ConversationMessage { ConversationId = convo.Id, Role = "user", Content = req.Message! };
            db.Messages.Add(userMsg);

            var privacy = await db.PrivacySettings.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
            var ctx = new UserAiContext(userId, privacy?.MemoryEnabled ?? false, privacy?.AllowProviderDataSharing ?? false);
            AssistantResult result;
            try { result = await assistant.RespondAsync(new AssistantRequest(ctx, req.Message!, history), ct); }
            catch (NoEligibleModelException ex) { return Results.Problem(statusCode: 409, title: ex.Message); }
            var reply = new ConversationMessage
            {
                ConversationId = convo.Id, Role = "assistant", Content = result.Text, Provider = result.Model,
                CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(1)
            };
            db.Messages.Add(reply);
            audit.Record(userId, "assistant.message", nameof(Conversation), convo.Id.ToString(),
                $"model={result.Model};simulated={result.IsSimulated};in={result.InputTokens};out={result.OutputTokens};flags={string.Join(',', result.GuardrailFlags)}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AssistantReply(convo.Id, reply.Id, result.Text, result.Model, result.IsSimulated));
        }).Validate<SendMessageRequest>();

        g.MapGet("/conversations", async (HttpContext http, DakshDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var items = await db.Conversations.Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new ConversationSummary(c.Id, c.CreatedAt, c.Messages.Count)).ToListAsync(ct);
            return Results.Ok(items);
        });

        g.MapGet("/conversations/{id:guid}/messages", async (Guid id, HttpContext http, DakshDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == userId, ct))
                return Results.NotFound();
            var msgs = await db.Messages.Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt)
                .Select(m => new MessageDto(m.Id, m.Role, m.Content, m.CreatedAt)).ToListAsync(ct);
            return Results.Ok(msgs);
        });
    }
}
