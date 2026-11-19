using Daksh.Ai;
using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using Daksh.Platform.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record SendMessageRequest(string? Message, Guid? ConversationId);
public sealed record AssistantReply(Guid ConversationId, Guid MessageId, string Reply, string Provider, bool IsSimulated, IReadOnlyList<string>? Sources = null);
public sealed record ConversationSummary(Guid Id, DateTimeOffset CreatedAt, int MessageCount);
public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);

public sealed class SendMessageValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageValidator() =>
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
}

[BsonIgnoreExtraElements]
public sealed class AssistantConversationDoc
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[BsonIgnoreExtraElements]
public sealed class AssistantMessageDoc
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string ConversationId { get; set; }
    public required string UserId { get; set; }
    public required string Role { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The Ask box. A question goes to the AI model together with whatever the owner's connected apps hold that is relevant to it
/// (recent chats, a named person's messages, the inbox, open tasks), gathered read-only for that one question. The owner must have
/// allowed the AI provider to see their data first; every question is logged (which apps were looked at, never their contents).
/// </summary>
public static class AssistantModule
{
    public const string ConsentCode = "ai_consent_required";

    public static void MapAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/assistant").RequireAuthorization().WithTags("Assistant");
        g.MapPost("/messages", SendAsync).Validate<SendMessageRequest>();
        g.MapGet("/conversations", ListConversationsAsync);
        g.MapGet("/conversations/{id:guid}/messages", ListMessagesAsync);
    }

    private static async Task<IResult> SendAsync(SendMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, IAssistantOrchestrator orchestrator,
        ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger("Assistant");
        var privacy = await PrivacyStore.GetAsync(db, who.UserId, ct);
        if (!privacy.AllowProviderDataSharing)
            return Results.Problem(statusCode: 403, title: "To answer, Daksha needs to send your question (and the app data it uses) to its AI provider. Allow that to continue.",
                extensions: new Dictionary<string, object?> { ["code"] = ConsentCode });

        var conversations = db.GetCollection<AssistantConversationDoc>("assistantConversations");
        var messages = db.GetCollection<AssistantMessageDoc>("assistantMessages");
        var conversationId = req.ConversationId?.ToString();
        if (conversationId is null || await conversations.Find(c => c.Id == conversationId && c.UserId == who.UserId).AnyAsync(ct) is false)
        {
            var created = new AssistantConversationDoc { UserId = who.UserId };
            await conversations.InsertOneAsync(created, cancellationToken: ct);
            conversationId = created.Id;
        }

        var question = req.Message!.Trim();
        var history = (await messages.Find(m => m.ConversationId == conversationId && m.UserId == who.UserId).SortByDescending(m => m.CreatedAt).Limit(12).ToListAsync(ct))
            .OrderBy(m => m.CreatedAt).Select(m => (m.Role, m.Content)).ToList();

        AssistantContextResult context;
        try { context = await AssistantContextBuilder.BuildAsync(db, who.UserId, question, () => GmailIntegrationModule.RecentForAssistantAsync(who.UserId, "in:inbox newer_than:7d", 10, db, protector, config, clients, ct), ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not gather app data for the assistant; answering without it"); context = new AssistantContextResult("", []); }

        AssistantResult result;
        try { result = await orchestrator.RespondAsync(new AssistantRequest(new UserAiContext(who.UserId, privacy.MemoryEnabled, AllowProviderDataSharing: true), question, history, context.Text), ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The AI model did not answer");
            return Results.Problem(statusCode: 502, title: "The AI service did not answer just now. Try again in a moment.");
        }

        var now = DateTime.UtcNow;
        var userDoc = new AssistantMessageDoc { ConversationId = conversationId, UserId = who.UserId, Role = "user", Content = question, CreatedAt = now };
        var replyDoc = new AssistantMessageDoc { ConversationId = conversationId, UserId = who.UserId, Role = "assistant", Content = result.Text, CreatedAt = now.AddMilliseconds(1) };
        await messages.InsertManyAsync([userDoc, replyDoc], cancellationToken: ct);
        await AuditStore.WriteAsync(db, who.UserId, "assistant.ask", "assistant", conversationId,
            context.Sources.Count == 0 ? "Answered without looking at any connected app" : "Looked at: " + string.Join(", ", context.Sources), ct);

        return Results.Ok(new AssistantReply(Guid.Parse(conversationId), Guid.Parse(replyDoc.Id), result.Text, result.Model, result.IsSimulated, context.Sources));
    }

    private static async Task<IResult> ListConversationsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var list = await db.GetCollection<AssistantConversationDoc>("assistantConversations").Find(c => c.UserId == who.UserId).SortByDescending(c => c.CreatedAt).Limit(50).ToListAsync(ct);
        var counts = new List<ConversationSummary>();
        foreach (var c in list)
            counts.Add(new ConversationSummary(Guid.Parse(c.Id), new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)),
                (int)await db.GetCollection<AssistantMessageDoc>("assistantMessages").CountDocumentsAsync(m => m.ConversationId == c.Id, cancellationToken: ct)));
        return Results.Ok(counts);
    }

    private static async Task<IResult> ListMessagesAsync(Guid id, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var key = id.ToString();
        if (await db.GetCollection<AssistantConversationDoc>("assistantConversations").Find(c => c.Id == key && c.UserId == who.UserId).AnyAsync(ct) is false) return Results.NotFound();
        var list = await db.GetCollection<AssistantMessageDoc>("assistantMessages").Find(m => m.ConversationId == key && m.UserId == who.UserId).SortBy(m => m.CreatedAt).ToListAsync(ct);
        return Results.Ok(list.Select(m => new MessageDto(Guid.Parse(m.Id), m.Role, m.Content, new DateTimeOffset(DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc)))));
    }
}
