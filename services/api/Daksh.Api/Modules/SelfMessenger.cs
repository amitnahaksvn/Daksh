using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>A person or chat the owner can message on one connected app.</summary>
public sealed record ChatTarget(string ChatId, string Name);

/// <summary>
/// What a messaging app (WhatsApp, Telegram, and any added later) must provide so the owner can say "message Ravi that I'll be late"
/// from their own chat. Name matching, ambiguity and error wording live in <see cref="SelfSend"/> once, so a new app only implements these two methods.
/// </summary>
public interface ISelfMessenger
{
    /// <summary>The app's name as the owner knows it, for replies ("WhatsApp").</summary>
    string AppName { get; }
    /// <summary>Chats whose name matches <paramref name="query"/> (case-insensitive; exact matches are preferred by the caller), or the chat for a phone number if the app can message one.</summary>
    Task<IReadOnlyList<ChatTarget>> FindAsync(string query, CancellationToken ct);
    /// <summary>Sends <paramref name="text"/> as the owner.</summary>
    Task SendAsync(string chatId, string text, CancellationToken ct);
}

public static class SelfSend
{
    /// <summary>Finds who the owner means, sends, and returns what to tell them (sent, not found, or which one did they mean).</summary>
    public static async Task<string> RunAsync(ISelfMessenger app, string to, string text, CancellationToken ct)
    {
        var q = to.Trim();
        var found = await app.FindAsync(q, ct);
        var exact = found.Where(t => t.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0 ? exact : found.ToList();
        if (hits.Count == 0) return $"ℹ️ I couldn't find \"{q}\" on {app.AppName}, so I didn't send anything. Tell me the full name as saved.";
        var names = hits.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 1) return $"ℹ️ \"{q}\" matches more than one person on {app.AppName} ({string.Join(", ", names.Take(5))}). I didn't send anything: tell me which one.";
        try { await app.SendAsync(hits[0].ChatId, text, ct); }
        catch (Exception) { return $"ℹ️ I couldn't send that to {hits[0].Name} on {app.AppName} right now. The app may be disconnected."; }
        return $"✅ Sent to {hits[0].Name} on {app.AppName}: \"{(text.Length > 200 ? text[..200] + "…" : text)}\"";
    }
}

public sealed class WhatsAppSelfMessenger(IMongoDatabase db, WhatsAppIntegrationProvider whatsApp, IntegrationConnection connection) : ISelfMessenger
{
    public string AppName => "WhatsApp";

    public async Task<IReadOnlyList<ChatTarget>> FindAsync(string query, CancellationToken ct)
    {
        var digits = new string(query.Where(char.IsDigit).ToArray());
        if (digits.Length >= 8 && !query.Any(char.IsLetter)) return [new(digits + "@s.whatsapp.net", "+" + digits)];
        var all = await db.GetCollection<WhatsAppContact>("whatsappContacts").Find(c => c.ConnectionId == connection.Id).ToListAsync(ct);
        return all.Select(c => new ChatTarget(c.ContactId, !string.IsNullOrWhiteSpace(c.Name) ? c.Name! : c.Notify ?? ""))
            .Where(t => t.Name.Length > 0 && t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public Task SendAsync(string chatId, string text, CancellationToken ct) =>
        whatsApp.SendMessageAsync(connection.Id, chatId, text, quotedMessageId: null, ct, isAiGenerated: false);
}

public sealed class TelegramSelfMessenger(IMongoDatabase db, TelegramIntegrationProvider telegram, IntegrationConnection connection) : ISelfMessenger
{
    public string AppName => "Telegram";

    /// <summary>Telegram can only message someone the account already has a chat with, so names come from the person's existing 1:1 chats.</summary>
    public async Task<IReadOnlyList<ChatTarget>> FindAsync(string query, CancellationToken ct)
    {
        var rows = await db.GetCollection<TelegramMessage>("telegramMessages")
            .Find(m => m.ConnectionId == connection.Id && !m.IsGroup && m.ChatName != null).SortByDescending(m => m.Timestamp).Limit(3000).ToListAsync(ct);
        return rows.GroupBy(m => m.ChatId).Select(g => new ChatTarget(g.Key, g.First().ChatName!))
            .Where(t => t.ChatId != connection.SelfChatId && t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task SendAsync(string chatId, string text, CancellationToken ct) =>
        await TelegramIntegrationModule.SendAndStoreAsync(db, telegram, connection.Id, chatId, text, isAiGenerated: false, ct);
}
