using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record TelegramDeleteRequest(bool ForEveryone);
public sealed record TelegramEditRequest(string Text);
public sealed record TelegramForwardRequest(string ToChatId);
public sealed record TelegramReactRequest(string Emoji);
public sealed record TelegramTypingRequest(bool Typing);
public sealed record TelegramHistoryRequest(string? BeforeId);
public sealed record TelegramSearchHit(string MessageId, string ChatId, string? ChatName, bool FromMe, string Text, DateTime Timestamp);
public sealed record TelegramGroupMemberDto(string Id, string Name, bool IsAdmin);
public sealed record TelegramGroupInfoDto(string Subject, string? Description, IReadOnlyList<TelegramGroupMemberDto> Members);

/// <summary>
/// Telegram features beyond plain text: attachments, read ticks, deleting and editing, reactions, forwarding, typing, group details, older
/// history, search, and pin / archive / mute. Each is an owner-checked wrapper around the Telegram service or the shared message store.
/// </summary>
public static class TelegramFeaturesModule
{
    private const long MaxUploadBytes = 14L * 1024 * 1024;

    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{connectionId}/chats/{chatId}/media", SendMediaAsync).DisableAntiforgery();
        g.MapPost("/{connectionId}/chats/{chatId}/read", ReadAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/typing", TypingAsync);
        g.MapGet("/{connectionId}/chats/{chatId}/presence", PresenceAsync);
        g.MapGet("/{connectionId}/chats/{chatId}/group", GroupAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/history", HistoryAsync);
        g.MapPut("/{connectionId}/chats/{chatId}/prefs", PrefsAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/messages/{messageId}/delete", DeleteAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/messages/{messageId}/edit", EditAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/messages/{messageId}/forward", ForwardAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/messages/{messageId}/react", ReactAsync);
        g.MapPost("/{connectionId}/chats/{chatId}/messages/{messageId}/star", StarAsync);
        g.MapGet("/{connectionId}/search", SearchAsync);
    }

    private static IResult Fail(Exception ex, string title) =>
        ex is HttpRequestException ? Results.Problem(statusCode: 502, title: "The Telegram service is not running.") : Results.Problem(statusCode: 409, title: title, detail: ex.Message);

    private static IMongoCollection<TelegramMessage> Messages(IMongoDatabase db) => db.GetCollection<TelegramMessage>("telegramMessages");
    private static string Path(string connectionId, string tail) => $"/sessions/{connectionId}/{tail}";

    private static async Task<IResult> SendMediaAsync(string connectionId, string chatId, [FromForm] IFormFile file, [FromForm] string? caption, [FromForm] string? replyToId,
        IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "Choose a file to send.");
        if (file.Length > MaxUploadBytes) return Results.Problem(statusCode: 413, title: "That file is too large to send (limit 14 MB).");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        try
        {
            var sent = await provider.CallAsync(Path(connectionId, "send-media"), new { chatId, data = Convert.ToBase64String(ms.ToArray()), fileName = file.FileName, mimeType = mime, caption, replyToId }, ct);
            var messageId = sent.GetProperty("messageId").GetString()!;
            var kind = sent.GetProperty("mediaKind").GetString();
            var text = sent.GetProperty("text").GetString() ?? "";
            var when = sent.GetProperty("timestamp").GetDateTime().ToUniversalTime();
            var prior = await Messages(db).Find(m => m.ConnectionId == connectionId && m.ChatId == chatId).SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
            await Messages(db).UpdateOneAsync(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.MessageId == messageId,
                Builders<TelegramMessage>.Update.Set(m => m.FromMe, true).Set(m => m.HasMedia, true).Set(m => m.MediaKind, kind).Set(m => m.MimeType, mime).Set(m => m.FileName, file.FileName)
                    .SetOnInsert(m => m.ChatName, prior?.ChatName).SetOnInsert(m => m.IsGroup, prior?.IsGroup ?? false).SetOnInsert(m => m.SenderName, "Me")
                    .SetOnInsert(m => m.Text, text).SetOnInsert(m => m.Timestamp, when).SetOnInsert(m => m.Status, 2).SetOnInsert(m => m.ReplyToId, replyToId),
                new UpdateOptions { IsUpsert = true }, ct);
            return Results.NoContent();
        }
        catch (Exception ex) { return Fail(ex, "Could not send the file."); }
    }

    private static async Task<IResult> ReadAsync(string connectionId, string chatId, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { await provider.CallAsync(Path(connectionId, "read"), new { chatId }, ct); } catch (Exception ex) { return Fail(ex, "Could not mark the chat as read."); }
        return Results.NoContent();
    }

    private static async Task<IResult> TypingAsync(string connectionId, string chatId, TelegramTypingRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { await provider.CallAsync(Path(connectionId, "typing"), new { chatId, typing = req.Typing }, ct); } catch { /* a nicety: never an error */ }
        return Results.NoContent();
    }

    private static async Task<IResult> PresenceAsync(string connectionId, string chatId, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { return Results.Json(await provider.CallAsync($"/sessions/{connectionId}/presence?chatId={Uri.EscapeDataString(chatId)}", null, ct, get: true)); }
        catch { return Results.Json(new { state = "unknown" }); }
    }

    private static async Task<IResult> GroupAsync(string connectionId, string chatId, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try
        {
            var meta = await provider.CallAsync($"/sessions/{connectionId}/group?chatId={Uri.EscapeDataString(chatId)}", null, ct, get: true);
            var members = meta.GetProperty("members").EnumerateArray().Select(p => new TelegramGroupMemberDto(p.GetProperty("id").GetString() ?? "", p.GetProperty("name").GetString() ?? "", p.GetProperty("isAdmin").GetBoolean()))
                .OrderByDescending(m => m.IsAdmin).ThenBy(m => m.Name).ToList();
            var desc = meta.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return Results.Ok(new TelegramGroupInfoDto(meta.GetProperty("subject").GetString() ?? "", desc, members));
        }
        catch (Exception ex) { return Fail(ex, "Could not load the group."); }
    }

    /// <summary>Asks Telegram for messages older than the oldest one we have, so they get stored; the client then reloads the thread.</summary>
    private static async Task<IResult> HistoryAsync(string connectionId, string chatId, TelegramHistoryRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try
        {
            var oldest = req.BeforeId ?? (await Messages(db).Find(m => m.ConnectionId == connectionId && m.ChatId == chatId).SortBy(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct))?.MessageId;
            var res = await provider.CallAsync(Path(connectionId, "history"), new { chatId, beforeId = oldest }, ct);
            return Results.Ok(new { fetched = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("fetched", out var f) ? f.GetInt32() : 0 });
        }
        catch (Exception ex) { return Fail(ex, "Could not load older messages."); }
    }

    private static async Task<IResult> PrefsAsync(string connectionId, string chatId, ChatPrefRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var update = Builders<WhatsAppChatPref>.Update.SetOnInsert(p => p.ConnectionId, connectionId).SetOnInsert(p => p.ChatId, chatId);
        if (req.Pinned is { } pinned) update = update.Set(p => p.Pinned, pinned);
        if (req.Archived is { } archived) update = update.Set(p => p.Archived, archived);
        if (req.MuteHours is { } hours) update = update.Set(p => p.MutedUntil, hours > 0 ? DateTime.UtcNow.AddHours(hours) : (DateTime?)null);
        await db.GetCollection<WhatsAppChatPref>("telegramChatPrefs").UpdateOneAsync(p => p.ConnectionId == connectionId && p.ChatId == chatId, update, new UpdateOptions { IsUpsert = true }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(string connectionId, string chatId, string messageId, TelegramDeleteRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var filter = Builders<TelegramMessage>.Filter.Where(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.MessageId == messageId);
        if (await Messages(db).Find(filter).AnyAsync(ct) is false) return Results.NotFound();
        try { await provider.CallAsync(Path(connectionId, "delete"), new { chatId, messageId, forEveryone = req.ForEveryone }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not delete the message."); }
        await Messages(db).UpdateOneAsync(filter, req.ForEveryone
            ? Builders<TelegramMessage>.Update.Set(m => m.Deleted, true).Set(m => m.Text, "This message was deleted").Set(m => m.HasMedia, false)
            : Builders<TelegramMessage>.Update.Set(m => m.Hidden, true), cancellationToken: ct);
        if (req.ForEveryone) await db.GetCollection<MongoDB.Bson.BsonDocument>("telegramMedia").DeleteOneAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.And(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("connectionId", connectionId), Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("chatId", chatId), Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("messageId", messageId)), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> EditAsync(string connectionId, string chatId, string messageId, TelegramEditRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "The message cannot be empty.");
        var message = await Messages(db).Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.MessageId == messageId).FirstOrDefaultAsync(ct);
        if (message is null || !message.FromMe || message.Deleted) return Results.Problem(statusCode: 400, title: "Only your own messages can be edited.");
        try { await provider.CallAsync(Path(connectionId, "edit"), new { chatId, messageId, text = req.Text.Trim() }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not edit the message."); }
        await Messages(db).UpdateOneAsync(m => m.Id == message.Id, Builders<TelegramMessage>.Update.Set(m => m.Text, req.Text.Trim()).Set(m => m.Edited, true), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ForwardAsync(string connectionId, string chatId, string messageId, TelegramForwardRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.ToChatId)) return Results.Problem(statusCode: 400, title: "Choose a chat to forward to.");
        try { await provider.CallAsync(Path(connectionId, "forward"), new { fromChatId = chatId, messageId, toChatId = req.ToChatId }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not forward the message."); }
        return Results.NoContent();
    }

    private static async Task<IResult> ReactAsync(string connectionId, string chatId, string messageId, TelegramReactRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { await provider.CallAsync(Path(connectionId, "react"), new { chatId, messageId, emoji = req.Emoji }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not react to the message."); }
        return Results.NoContent();
    }

    private static async Task<IResult> StarAsync(string connectionId, string chatId, string messageId, StarMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var res = await Messages(db).UpdateOneAsync(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.MessageId == messageId, Builders<TelegramMessage>.Update.Set(m => m.Starred, req.Starred), cancellationToken: ct);
        return res.MatchedCount == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> SearchAsync(string connectionId, string q, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await TelegramIntegrationModule.OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        q = (q ?? "").Trim();
        if (q.Length < 2) return Results.Ok(Array.Empty<TelegramSearchHit>());
        var filter = Builders<TelegramMessage>.Filter.And(
            Builders<TelegramMessage>.Filter.Eq(m => m.ConnectionId, connectionId), Builders<TelegramMessage>.Filter.Eq(m => m.Hidden, false), Builders<TelegramMessage>.Filter.Eq(m => m.Deleted, false),
            Builders<TelegramMessage>.Filter.Regex(m => m.Text, new MongoDB.Bson.BsonRegularExpression(Regex.Escape(q), "i")));
        var hits = await Messages(db).Find(filter).SortByDescending(m => m.Timestamp).Limit(50).ToListAsync(ct);
        return Results.Ok(hits.Select(m => new TelegramSearchHit(m.MessageId, m.ChatId, m.ChatName, m.FromMe, m.Text, m.Timestamp)));
    }
}
