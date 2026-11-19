using Daksh.Ai;
using Daksh.Api.Auth;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record ChatAssistRequest(string Channel, string ConnectionId, string ChatId, string Mode, string? MessageId = null, string? Language = null);
public sealed record ChatAssistResponse(string Mode, string? Text, IReadOnlyList<string> Suggestions, bool IsSimulated);

/// <summary>
/// AI help inside a conversation, only ever on request: a short summary, suggested replies for the owner to pick from (nothing is sent), or a
/// translation of one message. The conversation text goes to the AI provider only because the owner clicked the button.
/// </summary>
public static class ChatAssistModule
{
    public static void MapChatAssist(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/ai").WithTags("Chat assist").RequireAuthorization(AuthSetup.UserScheme);
        g.MapPost("/chat-assist", AssistAsync);
    }

    /// <summary>One line per message, oldest first, labelled so the model knows who said what.</summary>
    public static string Transcript(IEnumerable<(bool FromMe, string Text, DateTime At)> messages) =>
        string.Join("\n", messages.OrderBy(m => m.At).Select(m => $"{(m.FromMe ? "Me" : "Them")}: {m.Text.Replace('\n', ' ')}"));

    public static string PromptFor(string mode, string transcript, string? target, string? language) => mode switch
    {
        "summary" => "Summarize this chat in three or four short bullet points: what it is about, anything I promised or was asked, and anything still open. " +
                     "Output only the bullet points.\n\n" + transcript,
        "suggest" => "You are helping me, the account owner, answer this chat. Write three different short replies I could send next, in the same language and tone as the conversation. " +
                     "They must not promise, agree to or decide anything on my behalf that the conversation does not already settle. Output exactly three lines, one reply per line, with no numbering and no extra text.\n\n" + transcript,
        "translate" => $"Translate the following message into {(string.IsNullOrWhiteSpace(language) ? "English" : language)}. Output only the translation.\n\n{target}",
        _ => throw new ArgumentException("Unknown mode."),
    };

    /// <summary>Turns a model's three-lines answer into clean suggestions.</summary>
    public static IReadOnlyList<string> ParseSuggestions(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^(\d+[\.\)]|[-*•])\s*", "").Trim().Trim('"', '“', '”'))
            .Where(l => l.Length > 0).Take(3).ToList();

    private static async Task<IResult> AssistAsync(ChatAssistRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, IAssistantOrchestrator orchestrator, CancellationToken ct)
    {
        if (req.Mode is not ("summary" or "suggest" or "translate")) return Results.Problem(statusCode: 400, title: "Unknown mode.");
        if (req.Channel is not ("whatsapp" or "telegram")) return Results.Problem(statusCode: 400, title: "Unknown channel.");

        var rows = new List<(bool FromMe, string Text, DateTime At, string MessageId)>();
        if (req.Channel == "whatsapp")
        {
            if (await IntegrationsModule.OwnedConnectionAsync(db, req.ConnectionId, who.UserId, ct) is null) return Results.NotFound();
            var list = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(m => m.ConnectionId == req.ConnectionId && m.ChatId == req.ChatId && !m.Hidden && !m.Deleted).SortByDescending(m => m.Timestamp).Limit(60).ToListAsync(ct);
            rows.AddRange(list.Select(m => (m.FromMe, m.Text, m.Timestamp, m.MessageId)));
        }
        else
        {
            if (await TelegramIntegrationModule.OwnedAsync(db, req.ConnectionId, who.UserId, ct) is null) return Results.NotFound();
            var list = await db.GetCollection<TelegramMessage>("telegramMessages").Find(m => m.ConnectionId == req.ConnectionId && m.ChatId == req.ChatId && !m.Hidden && !m.Deleted).SortByDescending(m => m.Timestamp).Limit(60).ToListAsync(ct);
            rows.AddRange(list.Select(m => (m.FromMe, m.Text, m.Timestamp, m.MessageId)));
        }
        if (rows.Count == 0) return Results.Problem(statusCode: 400, title: "There are no messages to work with yet.");

        string prompt;
        if (req.Mode == "translate")
        {
            var target = rows.FirstOrDefault(r => r.MessageId == req.MessageId);
            if (target.MessageId is null) return Results.NotFound();
            prompt = PromptFor("translate", "", target.Text, req.Language);
        }
        else prompt = PromptFor(req.Mode, Transcript(rows.Select(r => (r.FromMe, r.Text, r.At))), null, null);

        // The owner asked for this by clicking the button, which is the consent to share this one conversation with the AI provider.
        var result = await orchestrator.RespondAsync(new AssistantRequest(new UserAiContext(who.UserId, MemoryEnabled: false, AllowProviderDataSharing: true), prompt), ct);
        if (result.IsSimulated || string.IsNullOrWhiteSpace(result.Text))
            return Results.Problem(statusCode: 503, title: "No AI model is set up, so this is not available.");
        return Results.Ok(req.Mode == "suggest"
            ? new ChatAssistResponse("suggest", null, ParseSuggestions(result.Text), false)
            : new ChatAssistResponse(req.Mode, result.Text.Trim(), [], false));
    }
}
