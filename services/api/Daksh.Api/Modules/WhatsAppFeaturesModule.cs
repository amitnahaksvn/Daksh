using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>Per-chat display choices that live in Daksha only: pinned, archived, muted. Written and read by this API.</summary>
[BsonIgnoreExtraElements]
public sealed class WhatsAppChatPref
{
    [BsonId, BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)] public string Id { get; set; } = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
    [BsonElement("connectionId")] public required string ConnectionId { get; set; }
    [BsonElement("chatId")] public required string ChatId { get; set; }
    [BsonElement("pinned")] public bool Pinned { get; set; }
    [BsonElement("archived")] public bool Archived { get; set; }
    [BsonElement("mutedUntil")] public DateTime? MutedUntil { get; set; }
}

public sealed record DeleteMessageRequest(bool ForEveryone);
public sealed record EditMessageRequest(string Text);
public sealed record ForwardMessageRequest(string ToChatId);
public sealed record StarMessageRequest(bool Starred);
public sealed record PresenceRequest(string State);
public sealed record ChatPrefRequest(bool? Pinned, bool? Archived, int? MuteHours);
public sealed record GroupActionRequest(string Action, string? Text, List<string>? Participants);
public sealed record DisappearingRequest(int Seconds);
public sealed record BlockRequest(bool Blocked);
public sealed record WhatsAppSearchHit(string MessageId, string ChatId, string? ChatName, bool FromMe, string Text, DateTime Timestamp);
public sealed record WhatsAppGroupMemberDto(string Id, string? Name, bool IsAdmin, bool IsMe);
public sealed record WhatsAppGroupInfoDto(string Subject, string? Description, DateTime? CreatedAt, IReadOnlyList<WhatsAppGroupMemberDto> Members);

/// <summary>
/// The WhatsApp features that go past plain text: attachments, read receipts, deleting and editing, forwarding, starring,
/// typing, group details, profile pictures, search, and pin / archive / mute. Each one is a thin, owner-checked wrapper
/// around the WhatsApp service or the shared message store.
/// </summary>
public static class WhatsAppFeaturesModule
{
    private const long MaxUploadBytes = 14L * 1024 * 1024;

    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/media", SendMediaAsync).DisableAntiforgery();
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/read", MarkReadAsync);
        g.MapPost("/whatsapp/{connectionId}/messages/{messageId}/delete", DeleteAsync);
        g.MapPost("/whatsapp/{connectionId}/messages/{messageId}/edit", EditAsync);
        g.MapPost("/whatsapp/{connectionId}/messages/{messageId}/forward", ForwardAsync);
        g.MapPost("/whatsapp/{connectionId}/messages/{messageId}/star", StarAsync);
        g.MapGet("/whatsapp/{connectionId}/chats/{chatId}/presence", GetPresenceAsync);
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/presence", SetPresenceAsync);
        g.MapGet("/whatsapp/{connectionId}/chats/{chatId}/group", GetGroupAsync);
        g.MapGet("/whatsapp/{connectionId}/chats/{chatId}/picture", GetPictureAsync);
        g.MapPut("/whatsapp/{connectionId}/chats/{chatId}/prefs", SetPrefsAsync);
        g.MapGet("/whatsapp/{connectionId}/search", SearchAsync);
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/group-action", GroupActionAsync);
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/disappearing", DisappearingAsync);
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/block", BlockAsync);
        g.MapPost("/whatsapp/{connectionId}/chats/{chatId}/clear", ClearAsync);
    }

    /// <summary>The media kind WhatsApp should send a file as, chosen from its type.</summary>
    public static string KindFor(string mimeType, bool asSticker = false) =>
        asSticker ? "sticker"
        : mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image"
        : mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video"
        : mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio"
        : "document";

    private static IResult Fail(Exception ex, string title) => Results.Problem(statusCode: 502, title: title, detail: ex.Message);

    private static async Task<IResult> SendMediaAsync(string connectionId, string chatId, [FromForm] IFormFile file, [FromForm] string? caption, [FromForm] string? quotedMessageId, [FromForm] bool? voiceNote,
        IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (file is null || file.Length == 0) return Results.Problem(statusCode: 400, title: "Choose a file to send.");
        if (file.Length > MaxUploadBytes) return Results.Problem(statusCode: 413, title: "That file is too large to send (limit 14 MB).");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        try { await whatsApp.SendMediaAsync(connectionId, chatId, KindFor(mime), ms.ToArray(), mime, file.FileName, caption, quotedMessageId, voiceNote ?? false, ct); }
        catch (Exception ex) { return Fail(ex, "Could not send the file."); }
        return Results.NoContent();
    }

    private static async Task<IResult> MarkReadAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { await whatsApp.MarkReadAsync(connectionId, chatId, ct); }
        catch (Exception ex) { return Fail(ex, "Could not mark the chat as read."); }
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(string connectionId, string messageId, DeleteMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var message = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(m => m.ConnectionId == connectionId && m.MessageId == messageId).FirstOrDefaultAsync(ct);
        if (message is null) return Results.NotFound();
        try { await whatsApp.DeleteMessageAsync(connectionId, message.ChatId, messageId, req.ForEveryone, ct); }
        catch (Exception ex) { return Fail(ex, "Could not delete the message."); }
        return Results.NoContent();
    }

    private static async Task<IResult> EditAsync(string connectionId, string messageId, EditMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "The message cannot be empty.");
        var message = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(m => m.ConnectionId == connectionId && m.MessageId == messageId).FirstOrDefaultAsync(ct);
        if (message is null) return Results.NotFound();
        try { await whatsApp.EditMessageAsync(connectionId, message.ChatId, messageId, req.Text.Trim(), ct); }
        catch (Exception ex) { return Fail(ex, "Could not edit the message."); }
        return Results.NoContent();
    }

    private static async Task<IResult> ForwardAsync(string connectionId, string messageId, ForwardMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.ToChatId)) return Results.Problem(statusCode: 400, title: "Choose a chat to forward to.");
        try { await whatsApp.ForwardMessageAsync(connectionId, req.ToChatId, messageId, ct); }
        catch (Exception ex) { return Fail(ex, "Could not forward the message."); }
        return Results.NoContent();
    }

    private static async Task<IResult> StarAsync(string connectionId, string messageId, StarMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var res = await db.GetCollection<WhatsAppMessage>("whatsappMessages").UpdateOneAsync(m => m.ConnectionId == connectionId && m.MessageId == messageId, Builders<WhatsAppMessage>.Update.Set(m => m.Starred, req.Starred), cancellationToken: ct);
        return res.MatchedCount == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> GetPresenceAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { return Results.Json(await whatsApp.GetPresenceAsync(connectionId, chatId, ct)); }
        catch { return Results.Json(new { state = "unknown" }); } // typing indicators are a nicety: never an error
    }

    private static async Task<IResult> SetPresenceAsync(string connectionId, string chatId, PresenceRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (req.State is not ("composing" or "paused" or "available")) return Results.Problem(statusCode: 400, title: "Unknown state.");
        try { await whatsApp.SetPresenceAsync(connectionId, chatId, req.State, ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    private static async Task<IResult> GetGroupAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        var connection = await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        if (!WhatsAppChat.IsGroup(chatId)) return Results.Problem(statusCode: 400, title: "That chat is not a group.");
        System.Text.Json.JsonElement meta;
        try { meta = await whatsApp.GetGroupAsync(connectionId, chatId, ct); }
        catch (Exception ex) { return Fail(ex, "Could not load the group."); }

        var contacts = await db.GetCollection<WhatsAppContact>("whatsappContacts").Find(c => c.ConnectionId == connectionId).ToListAsync(ct);
        string? NameOf(string id)
        {
            var c = contacts.FirstOrDefault(x => x.ContactId == id || x.Lid == id || x.PhoneNumber == id);
            return !string.IsNullOrWhiteSpace(c?.Name) ? c!.Name : c?.Notify;
        }
        var me = connection.AccountIdentifier?.Split(':', '@')[0];
        var members = meta.GetProperty("participants").EnumerateArray().Select(p =>
        {
            var id = p.GetProperty("id").GetString() ?? "";
            var phone = p.TryGetProperty("phoneNumber", out var ph) && ph.ValueKind == System.Text.Json.JsonValueKind.String ? ph.GetString() : null;
            var admin = p.TryGetProperty("admin", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String;
            var number = (phone ?? id).Split('@')[0];
            return new WhatsAppGroupMemberDto(id, NameOf(id) ?? (phone is null ? null : NameOf(phone)), admin, me is not null && number == me);
        }).OrderByDescending(m => m.IsAdmin).ThenBy(m => m.Name ?? "~").ToList();
        DateTime? created = meta.TryGetProperty("createdAt", out var cr) && cr.ValueKind == System.Text.Json.JsonValueKind.String && DateTime.TryParse(cr.GetString(), out var d) ? d.ToUniversalTime() : null;
        return Results.Ok(new WhatsAppGroupInfoDto(meta.GetProperty("subject").GetString() ?? "", meta.TryGetProperty("description", out var ds) && ds.ValueKind == System.Text.Json.JsonValueKind.String ? ds.GetString() : null, created, members));
    }

    private static async Task<IResult> GetPictureAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var pic = await whatsApp.GetProfilePictureAsync(connectionId, chatId, ct);
        return pic is null ? Results.NotFound() : Results.File(pic.Value.Data, pic.Value.MimeType);
    }

    private static async Task<IResult> SetPrefsAsync(string connectionId, string chatId, ChatPrefRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var update = Builders<WhatsAppChatPref>.Update.SetOnInsert(p => p.ConnectionId, connectionId).SetOnInsert(p => p.ChatId, chatId);
        if (req.Pinned is { } pinned) update = update.Set(p => p.Pinned, pinned);
        if (req.Archived is { } archived) update = update.Set(p => p.Archived, archived);
        if (req.MuteHours is { } hours) update = update.Set(p => p.MutedUntil, hours > 0 ? DateTime.UtcNow.AddHours(hours) : (DateTime?)null);
        await db.GetCollection<WhatsAppChatPref>("whatsappChatPrefs").UpdateOneAsync(p => p.ConnectionId == connectionId && p.ChatId == chatId, update, new UpdateOptions { IsUpsert = true }, ct);
        return Results.NoContent();
    }

    /// <summary>Finds messages containing the text, newest first, across every chat of this connection.</summary>
    private static async Task<IResult> SearchAsync(string connectionId, string q, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        q = (q ?? "").Trim();
        if (q.Length < 2) return Results.Ok(Array.Empty<WhatsAppSearchHit>());
        var filter = Builders<WhatsAppMessage>.Filter.And(
            Builders<WhatsAppMessage>.Filter.Eq(m => m.ConnectionId, connectionId),
            Builders<WhatsAppMessage>.Filter.Eq(m => m.Hidden, false),
            Builders<WhatsAppMessage>.Filter.Eq(m => m.Deleted, false),
            Builders<WhatsAppMessage>.Filter.Regex(m => m.Text, new MongoDB.Bson.BsonRegularExpression(Regex.Escape(q), "i")));
        var hits = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(filter).SortByDescending(m => m.Timestamp).Limit(50).ToListAsync(ct);
        return Results.Ok(hits.Select(m => new WhatsAppSearchHit(m.MessageId, m.ChatId, m.ChatName, m.FromMe, m.Text, m.Timestamp)));
    }

    private static async Task<IResult> GroupActionAsync(string connectionId, string chatId, GroupActionRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try
        {
            var res = await whatsApp.CallAsync($"/sessions/{connectionId}/group-action", new { chatId, action = req.Action, text = req.Text, participants = req.Participants }, ct);
            var link = res.ValueKind == System.Text.Json.JsonValueKind.Object && res.TryGetProperty("link", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String ? l.GetString() : null;
            return Results.Ok(new { link });
        }
        catch (Exception ex) { return Fail(ex, "That group change did not work."); }
    }

    private static async Task<IResult> DisappearingAsync(string connectionId, string chatId, DisappearingRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (req.Seconds is not (0 or 86400 or 604800 or 7776000)) return Results.Problem(statusCode: 400, title: "Choose Off, 24 hours, 7 days or 90 days.");
        try { await whatsApp.CallAsync($"/sessions/{connectionId}/disappearing", new { chatId, seconds = req.Seconds }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not change disappearing messages."); }
        return Results.NoContent();
    }

    private static async Task<IResult> BlockAsync(string connectionId, string chatId, BlockRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try { await whatsApp.CallAsync($"/sessions/{connectionId}/block", new { chatId, blocked = req.Blocked }, ct); }
        catch (Exception ex) { return Fail(ex, "Could not change the block."); }
        return Results.NoContent();
    }

    /// <summary>"Clear chat": hides every message of this chat in Daksha only (WhatsApp is not touched).</summary>
    private static async Task<IResult> ClearAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await IntegrationsModule.OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        await db.GetCollection<WhatsAppMessage>("whatsappMessages").UpdateManyAsync(m => m.ConnectionId == connectionId && m.ChatId == chatId, Builders<WhatsAppMessage>.Update.Set(m => m.Hidden, true), cancellationToken: ct);
        return Results.NoContent();
    }
}
