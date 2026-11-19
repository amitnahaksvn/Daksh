using System.Collections.Concurrent;
using System.Net.Http.Json;
using Daksh.Ai;
using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed class TelegramReaction { public string Emoji { get; set; } = ""; public int Count { get; set; } public bool Mine { get; set; } }

[BsonIgnoreExtraElements]
public sealed class TelegramMessage
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string ChatId { get; set; }
    public string? ChatName { get; set; }
    public bool IsGroup { get; set; }
    public required string MessageId { get; set; }
    public bool FromMe { get; set; }
    /// <summary>True only for replies Daksha's AI wrote — lets auto-reply tell "you answered" from "the AI answered".</summary>
    public bool IsAiGenerated { get; set; }
    public string? SenderName { get; set; }
    public List<string> Tags { get; set; } = [];
    public required string Text { get; set; }
    /// <summary>True when the Node service stored the attachment's bytes in "telegramMedia".</summary>
    public bool HasMedia { get; set; }
    /// <summary>photo, video, audio, voice, document or sticker.</summary>
    public string? MediaKind { get; set; }
    public string? MimeType { get; set; }
    public string? FileName { get; set; }
    /// <summary>Outgoing only: 1 pending, 2 sent, 4 read.</summary>
    public int? Status { get; set; }
    public bool Deleted { get; set; }
    public bool Edited { get; set; }
    /// <summary>"Delete for me": hidden in Daksha only.</summary>
    public bool Hidden { get; set; }
    public bool Starred { get; set; }
    public string? ReplyToId { get; set; }
    public List<TelegramReaction> Reactions { get; set; } = [];
    public DateTime Timestamp { get; set; }
}

public sealed record TelegramPasswordRequest(string? Password);
public sealed record TelegramSendRequest(string? Text, string? ReplyToId = null);
public sealed record TelegramChatDto(string ChatId, string Name, bool IsGroup, string? LastMessagePreview, DateTime? LastMessageAt, int UnreadCount,
    bool Pinned = false, bool Archived = false, DateTime? MutedUntil = null);
public sealed record TelegramReactionDto(string Emoji, int Count, bool Mine);
public sealed record TelegramMessageDto(string MessageId, string ChatId, bool FromMe, bool IsAiGenerated, string? SenderName, string Text, DateTime Timestamp, bool HasMedia = false,
    string? MediaKind = null, string? MimeType = null, string? FileName = null, int? Status = null, bool Deleted = false, bool Edited = false, bool Starred = false,
    string? ReplyToId = null, string? ReplyToText = null, IReadOnlyList<TelegramReactionDto>? Reactions = null);
public sealed record TelegramState(string Status, string? Qr, DateTime? ExpiresAt, bool PasswordRequired, string? Error);

/// <summary>What the Telegram Node service POSTs back: either a connection-status change or one message
/// (live, or backfilled history).</summary>
public sealed record TelegramInternalEvent(
    string ConnectionId, string EventType, string? Status, string? AccountIdentifier, string? Error,
    string? MessageId, string? ChatId, string? ChatName, bool IsGroup, bool FromMe, string? SenderName,
    string? Text, DateTime? Timestamp, bool History, bool HasMedia = false, string? MediaKind = null, string? MimeType = null, string? FileName = null, string? ReplyToId = null, string? SelfChatId = null);

/// <summary>Talks to the Telegram Node service (GramJS / MTProto), which holds each user's signed-in session.</summary>
public sealed class TelegramIntegrationProvider(HttpClient http) : IIntegrationProvider
{
    public string IntegrationKey => TelegramIntegrationModule.IntegrationKey;

    public async Task ConnectAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/sessions/{connectionId}/connect", null, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<TelegramState> GetStateAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/state", ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TelegramState>(cancellationToken: ct))!;
    }

    public async Task<QrResponse> GetQrAsync(string connectionId, CancellationToken ct)
    {
        var s = await GetStateAsync(connectionId, ct);
        return new QrResponse(s.Qr, s.ExpiresAt, s.Status);
    }

    public async Task<ConnectionStatus> GetStatusAsync(string connectionId, CancellationToken ct)
    {
        try
        {
            var s = await GetStateAsync(connectionId, ct);
            return Enum.TryParse<ConnectionStatus>(s.Status, out var status) ? status : ConnectionStatus.Error;
        }
        catch (HttpRequestException) { return ConnectionStatus.Error; }
    }

    public async Task<bool> SubmitPasswordAsync(string connectionId, string password, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/password", new { password }, ct);
        res.EnsureSuccessStatusCode();
        return true;
    }

    public async Task ReconnectAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/sessions/{connectionId}/reconnect", null, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/sessions/{connectionId}/disconnect", null, ct);
        res.EnsureSuccessStatusCode();
    }

    private sealed record SendReply(string MessageId, DateTime Timestamp);
    private sealed record ErrorBody(string? Error);

    /// <summary>One call to the Telegram service: POST a JSON body (or GET when <paramref name="body"/> is null and <paramref name="get"/> is set). Errors carry the service's own message.</summary>
    public async Task<System.Text.Json.JsonElement> CallAsync(string path, object? body, CancellationToken ct, bool get = false)
    {
        using var res = get ? await http.GetAsync(path, ct) : await http.PostAsJsonAsync(path, body ?? new { }, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var message = text;
            try { using var d = System.Text.Json.JsonDocument.Parse(text); if (d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) message = e.GetString() ?? text; } catch { /* raw text */ }
            throw new InvalidOperationException(message);
        }
        return string.IsNullOrWhiteSpace(text) ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The chat's profile (or group) picture, or null when it has none.</summary>
    public async Task<byte[]?> GetProfilePictureAsync(string connectionId, string chatId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/profile-picture?chatId={Uri.EscapeDataString(chatId)}", ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task<(string MessageId, DateTime Timestamp)> SendAsync(string connectionId, string chatId, string text, CancellationToken ct, string? replyToId = null)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/send", new { chatId, text, replyToId }, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken: ct);
            throw new InvalidOperationException(err?.Error ?? "Telegram did not accept the message.");
        }
        var sent = (await res.Content.ReadFromJsonAsync<SendReply>(cancellationToken: ct))!;
        return (sent.MessageId, sent.Timestamp);
    }
}

/// <summary>
/// Telegram as a personal account, like WhatsApp: the user scans a QR code with their Telegram app (and, if
/// they use two-step verification, enters their cloud password). A Node service (GramJS / MTProto) keeps the
/// signed-in session; this module stores messages it reports, shows them, and lets Daksha's assistant reply.
/// </summary>
public static class TelegramIntegrationModule
{
    public const string IntegrationKey = "telegram";

    public static IServiceCollection AddTelegramIntegration(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<TelegramIntegrationProvider>(c =>
        {
            c.BaseAddress = new Uri(config["Integrations:Telegram:BaseUrl"] ?? "http://localhost:4200");
            c.DefaultRequestHeaders.Add("X-Internal-Key", config["Integrations:Telegram:InternalServiceKey"] ?? "");
        });
        services.AddSingleton<IIntegrationProvider>(sp => sp.GetRequiredService<TelegramIntegrationProvider>());
        return services;
    }

    public static void MapTelegramIntegration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/telegram").WithTags("Telegram");
        var authed = g.RequireAuthorization(AuthSetup.UserScheme);
        authed.MapPost("/connect", ConnectAsync);
        authed.MapGet("/{connectionId}/state", StateAsync);
        authed.MapPost("/{connectionId}/password", PasswordAsync);
        authed.MapGet("/{connectionId}/chats", ListChatsAsync);
        authed.MapGet("/{connectionId}/chats/{chatId}/messages", ListMessagesAsync);
        authed.MapPost("/{connectionId}/chats/{chatId}/messages", SendAsync);
        authed.MapGet("/{connectionId}/chats/{chatId}/picture", PictureAsync);
        authed.MapGet("/{connectionId}/media/{chatId}/{messageId}", MediaAsync);
        TelegramFeaturesModule.Map(authed);
        // System-to-system only, authenticated by the shared internal key (never the user JWT).
        g.MapPost("/internal/events", ReceiveEventAsync).AllowAnonymous();
    }

    public static async Task EnsureTelegramIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        var messages = db.GetCollection<TelegramMessage>("telegramMessages");
        await messages.Indexes.CreateOneAsync(new CreateIndexModel<TelegramMessage>(
            Builders<TelegramMessage>.IndexKeys.Ascending(m => m.ConnectionId).Ascending(m => m.ChatId).Ascending(m => m.MessageId),
            new CreateIndexOptions { Unique = true, Name = "ux_telegramMessages_connection_chat_message" }), cancellationToken: ct);
        await messages.Indexes.CreateOneAsync(new CreateIndexModel<TelegramMessage>(
            Builders<TelegramMessage>.IndexKeys.Ascending(m => m.ConnectionId).Descending(m => m.Timestamp),
            new CreateIndexOptions { Name = "ix_telegramMessages_connection_timestamp" }), cancellationToken: ct);
        await db.GetCollection<IntegrationCatalogEntry>("integrations").UpdateOneAsync(c => c.IntegrationKey == IntegrationKey,
            Builders<IntegrationCatalogEntry>.Update.Set(c => c.IsEnabled, true), cancellationToken: ct);
    }

    internal static Task<IntegrationConnection?> OwnedAsync(IMongoDatabase db, string connectionId, string userId, CancellationToken ct) =>
        db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.Id == connectionId && c.UserId == userId && c.IntegrationKey == IntegrationKey).FirstOrDefaultAsync(ct)!;

    // ---- Endpoints -----------------------------------------------------------------------------------

    private static async Task<IResult> ConnectAsync(IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(c => c.IntegrationKey == IntegrationKey).FirstOrDefaultAsync(ct);
        if (catalog is null || !catalog.IsEnabled) return Results.Problem(statusCode: 404, title: "Telegram is not available yet.");

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await connections.Find(c => c.UserId == who.UserId && c.IntegrationKey == IntegrationKey && c.Status != ConnectionStatus.Disconnected).FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        if (connection is null)
        {
            connection = new IntegrationConnection { UserId = who.UserId, IntegrationKey = IntegrationKey, Status = ConnectionStatus.Connecting, CreatedAt = now, UpdatedAt = now };
            await connections.InsertOneAsync(connection, cancellationToken: ct);
        }
        else if (connection.Status == ConnectionStatus.Error)
        {
            await connections.UpdateOneAsync(c => c.Id == connection.Id,
                Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connecting).Set(c => c.LastError, null).Set(c => c.UpdatedAt, now), cancellationToken: ct);
        }

        try { await provider.ConnectAsync(connection.Id, ct); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "The Telegram service is not running."); }
        return Results.Ok(new ConnectResponse(connection.Id, ConnectionStatus.Connecting.ToString()));
    }

    private static async Task<IResult> StateAsync(string connectionId, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        try
        {
            var state = await provider.GetStateAsync(connectionId, ct);
            return Results.Ok(state);
        }
        catch (HttpRequestException) { return Results.Ok(new TelegramState("Error", null, null, false, "The Telegram service is not running.")); }
    }

    private static async Task<IResult> PasswordAsync(string connectionId, TelegramPasswordRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        if (string.IsNullOrEmpty(req.Password)) return Results.Problem(statusCode: 400, title: "Password is required.");
        await provider.SubmitPasswordAsync(connectionId, req.Password, ct);
        return Results.Ok();
    }

    private static async Task<IResult> ListChatsAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        var recent = await db.GetCollection<TelegramMessage>("telegramMessages")
            .Find(m => m.ConnectionId == connectionId && !m.Hidden).SortByDescending(m => m.Timestamp).Limit(1500).ToListAsync(ct);
        var prefs = (await db.GetCollection<WhatsAppChatPref>("telegramChatPrefs").Find(p => p.ConnectionId == connectionId).ToListAsync(ct)).ToDictionary(p => p.ChatId);
        var lastViewed = connection.LastViewedAt ?? DateTime.MinValue;
        var chats = recent.GroupBy(m => m.ChatId).Select(g =>
        {
            var last = g.First();
            var name = g.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ChatName))?.ChatName ?? g.Key;
            prefs.TryGetValue(g.Key, out var pref);
            return new TelegramChatDto(g.Key, name, last.IsGroup, last.Text, last.Timestamp, g.Count(m => !m.FromMe && m.Timestamp > lastViewed), pref?.Pinned ?? false, pref?.Archived ?? false, pref?.MutedUntil);
        }).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.LastMessageAt).ToList();
        return Results.Ok(chats);
    }

    private static async Task<IResult> ListMessagesAsync(string connectionId, string chatId, int? limit, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        var page = await db.GetCollection<TelegramMessage>("telegramMessages")
            .Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && !m.Hidden).SortByDescending(m => m.Timestamp).Limit(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(ct);
        page.Reverse();
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == connectionId,
            Builders<IntegrationConnection>.Update.Set(c => c.LastViewedAt, DateTime.UtcNow), cancellationToken: ct);
        var byId = page.ToDictionary(m => m.MessageId, m => m.Text);
        return Results.Ok(page.Select(m => ToDto(m, m.ReplyToId is not null ? byId.GetValueOrDefault(m.ReplyToId) : null)));
    }

    internal static TelegramMessageDto ToDto(TelegramMessage m, string? replyToText = null) => new(m.MessageId, m.ChatId, m.FromMe, m.IsAiGenerated, m.SenderName, m.Text, m.Timestamp, m.HasMedia,
        m.MediaKind, m.MimeType, m.FileName, m.Status, m.Deleted, m.Edited, m.Starred, m.ReplyToId, replyToText, m.Reactions.Select(r => new TelegramReactionDto(r.Emoji, r.Count, r.Mine)).ToList());

    private static async Task<IResult> PictureAsync(string connectionId, string chatId, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        try
        {
            var data = await provider.GetProfilePictureAsync(connectionId, chatId, ct);
            return data is null ? Results.NotFound() : Results.File(data, "image/jpeg");
        }
        catch (HttpRequestException) { return Results.NotFound(); } // the Telegram service is down: no picture, not an error
    }

    private static async Task<IResult> MediaAsync(string connectionId, string chatId, string messageId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var doc = await db.GetCollection<MongoDB.Bson.BsonDocument>("telegramMedia").Find(Builders<MongoDB.Bson.BsonDocument>.Filter.And(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("connectionId", connectionId),
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("chatId", chatId),
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("messageId", messageId))).FirstOrDefaultAsync(ct);
        if (doc is null || !doc.TryGetValue("data", out var data) || !data.IsBsonBinaryData) return Results.NotFound();
        var mime = doc.TryGetValue("mimeType", out var m) && m.IsString ? m.AsString : "image/jpeg";
        var message = await db.GetCollection<TelegramMessage>("telegramMessages").Find(x => x.ConnectionId == connectionId && x.ChatId == chatId && x.MessageId == messageId).FirstOrDefaultAsync(ct);
        return message?.MediaKind == "document"
            ? Results.File(data.AsBsonBinaryData.Bytes, mime, message.FileName ?? "file")
            : Results.File(data.AsBsonBinaryData.Bytes, mime, enableRangeProcessing: true);
    }

    private static async Task<IResult> SendAsync(string connectionId, string chatId, TelegramSendRequest req, IMongoDatabase db, TelegramIntegrationProvider provider, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "Message text is required.");
        try { return Results.Ok(await SendAndStoreAsync(db, provider, connection.Id, chatId, req.Text.Trim(), isAiGenerated: false, ct, req.ReplyToId)); }
        catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, title: ex.Message); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "The Telegram service is not running."); }
    }

    internal static async Task<TelegramMessageDto> SendAndStoreAsync(IMongoDatabase db, TelegramIntegrationProvider provider, string connectionId, string chatId, string text, bool isAiGenerated, CancellationToken ct, string? replyToId = null)
    {
        var (messageId, timestamp) = await provider.SendAsync(connectionId, chatId, text, ct, replyToId);
        var existing = await db.GetCollection<TelegramMessage>("telegramMessages").Find(m => m.ConnectionId == connectionId && m.ChatId == chatId).SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
        var stored = new TelegramMessage
        {
            ConnectionId = connectionId, ChatId = chatId, ChatName = existing?.ChatName, IsGroup = existing?.IsGroup ?? false,
            MessageId = messageId, FromMe = true, IsAiGenerated = isAiGenerated, SenderName = "Me", Text = text, Timestamp = timestamp,
        };
        // The Node service also reports our own outgoing messages; whichever arrives first wins. Only an
        // AI-sent message needs its flag preserved, so write it with an upsert rather than an insert.
        await db.GetCollection<TelegramMessage>("telegramMessages").UpdateOneAsync(
            m => m.ConnectionId == connectionId && m.ChatId == chatId && m.MessageId == messageId,
            Builders<TelegramMessage>.Update
                .Set(m => m.IsAiGenerated, isAiGenerated).Set(m => m.FromMe, true)
                .SetOnInsert(m => m.ChatName, stored.ChatName).SetOnInsert(m => m.IsGroup, stored.IsGroup)
                .SetOnInsert(m => m.SenderName, "Me").SetOnInsert(m => m.Text, text).SetOnInsert(m => m.Timestamp, timestamp)
                .SetOnInsert(m => m.Status, 2).SetOnInsert(m => m.ReplyToId, replyToId),
            new UpdateOptions { IsUpsert = true }, ct);
        return new TelegramMessageDto(messageId, chatId, true, isAiGenerated, "Me", text, timestamp);
    }

    // ---- Events from the Node service ----------------------------------------------------------------

    private static async Task<IResult> ReceiveEventAsync(
        TelegramInternalEvent evt, IMongoDatabase db, IConfiguration config, HttpContext http,
        IHubContext<IntegrationsHub> hub, IServiceScopeFactory scopes, IEmailSender emailSender, ILoggerFactory loggers, CancellationToken ct)
    {
        var expected = config["Integrations:Telegram:InternalServiceKey"];
        if (string.IsNullOrEmpty(expected) || http.Request.Headers["X-Internal-Key"] != expected) return Results.Unauthorized();

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await connections.Find(c => c.Id == evt.ConnectionId && c.IntegrationKey == IntegrationKey).FirstOrDefaultAsync(ct);
        if (connection is null) return Results.NotFound();

        if (evt.EventType == "Status")
        {
            var update = Builders<IntegrationConnection>.Update.Set(c => c.UpdatedAt, DateTime.UtcNow);
            if (Enum.TryParse<ConnectionStatus>(evt.Status, out var status))
            {
                update = update.Set(c => c.Status, status);
                if (status == ConnectionStatus.Connected) update = update.Set(c => c.ConnectedAt, DateTime.UtcNow).Set(c => c.LastActiveAt, DateTime.UtcNow).Set(c => c.LastError, null);
                if (status == ConnectionStatus.Disconnected) update = update.Set(c => c.DisconnectedAt, DateTime.UtcNow);
            }
            if (evt.AccountIdentifier is not null) update = update.Set(c => c.AccountIdentifier, evt.AccountIdentifier);
            if (!string.IsNullOrEmpty(evt.SelfChatId)) update = update.Set(c => c.SelfChatId, evt.SelfChatId);
            if (evt.Error is not null) update = update.Set(c => c.LastError, evt.Error);
            await connections.UpdateOneAsync(c => c.Id == connection.Id, update, cancellationToken: ct);
            return Results.Ok();
        }

        // A stored message changed (read ticks, deleted, edited, reaction): tell open threads to refresh.
        if (evt.EventType == "MessageUpdated")
        {
            await IntegrationEvents.PublishAsync(hub, connection.UserId, "TelegramMessageUpdated", new { connectionId = evt.ConnectionId, chatId = evt.ChatId, messageId = evt.MessageId });
            return Results.Ok();
        }

        if (evt.EventType != "Message" || evt.MessageId is null || evt.ChatId is null || string.IsNullOrEmpty(evt.Text)) return Results.Ok();

        var stored = new TelegramMessage
        {
            ConnectionId = connection.Id, ChatId = evt.ChatId, ChatName = evt.ChatName, IsGroup = evt.IsGroup, MessageId = evt.MessageId,
            FromMe = evt.FromMe, SenderName = evt.SenderName, Text = evt.Text, HasMedia = evt.HasMedia, MediaKind = evt.MediaKind, MimeType = evt.MimeType, FileName = evt.FileName,
            ReplyToId = evt.ReplyToId, Status = evt.FromMe ? 2 : null, Timestamp = (evt.Timestamp ?? DateTime.UtcNow).ToUniversalTime(),
        };
        try { await db.GetCollection<TelegramMessage>("telegramMessages").InsertOneAsync(stored, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Already stored (e.g. we sent it ourselves, or it was redelivered): refresh the chat name only.
            if (!string.IsNullOrEmpty(evt.ChatName))
                await db.GetCollection<TelegramMessage>("telegramMessages").UpdateOneAsync(m => m.ConnectionId == connection.Id && m.ChatId == evt.ChatId && m.MessageId == evt.MessageId,
                    Builders<TelegramMessage>.Update.Set(m => m.ChatName, evt.ChatName), cancellationToken: ct);
            if (evt.HasMedia)
                await db.GetCollection<TelegramMessage>("telegramMessages").UpdateOneAsync(m => m.ConnectionId == connection.Id && m.ChatId == evt.ChatId && m.MessageId == evt.MessageId,
                    Builders<TelegramMessage>.Update.Set(m => m.HasMedia, true), cancellationToken: ct);
            return Results.Ok();
        }

        // Backfilled history fills the chat list quietly: no bell, no auto-reply.
        if (evt.History) return Results.Ok();

        await connections.UpdateOneAsync(c => c.Id == connection.Id, Builders<IntegrationConnection>.Update.Set(c => c.LastActiveAt, DateTime.UtcNow), cancellationToken: ct);

        // A message the owner types in their own "Saved Messages" may be a command ("busy for 2 hours", "buy milk"): do it and confirm there.
        if (!stored.IsGroup && !string.IsNullOrEmpty(connection.SelfChatId) && stored.ChatId == connection.SelfChatId
            && SelfCommands.Parse(stored.Text).Kind != SelfCommandKind.None && SelfCommandSeen.First(connection.Id, stored.MessageId))
        {
            var confirmation = await SelfCommands.RunAsync(db, connection.UserId, stored.Text, DateTime.UtcNow, ct);
            if (confirmation is not null)
                try
                {
                    using var scope = scopes.CreateScope();
                    await SendAndStoreAsync(db, scope.ServiceProvider.GetRequiredService<TelegramIntegrationProvider>(), connection.Id, stored.ChatId, confirmation, isAiGenerated: true, ct);
                }
                catch (Exception ex) { loggers.CreateLogger("SelfCommands").LogWarning(ex, "Could not confirm a self-command on Telegram for connection {ConnectionId}", connection.Id); }
            return Results.Ok();
        }

        // From here on Telegram runs through the same settings, rules and intelligence as WhatsApp — the
        // settings documents, chat rules, automation rules and insights are all keyed by connection id.
        var settings = await WhatsAppSettingsModule.GetOrCreateAsync(db, connection.Id, ct);
        var logger = loggers.CreateLogger("TelegramAutoReply");
        var isReceived = !stored.FromMe;
        var chatRule = await WhatsAppAutomationModule.GetChatRuleAsync(db, connection.Id, stored.ChatId, ct);
        var highPriority = chatRule?.Priority == "High";
        var isSavedContact = !stored.IsGroup; // Telegram only lists chats the user already has
        var monitored = chatRule?.Monitor ?? true;
        var passesMonitoring = monitored && (!isReceived || WhatsAppMonitoring.Passes(settings, stored.IsGroup, stored.Text, isSavedContact, isBusiness: false));

        var tags = new List<string>();
        var matchedRules = new List<WhatsAppAutomationRule>();
        if (isReceived && monitored)
        {
            tags = WhatsAppIntel.Detect(stored.Text, settings.DisabledIntelligenceTags);
            await db.GetCollection<TelegramMessage>("telegramMessages").UpdateOneAsync(m => m.Id == stored.Id, Builders<TelegramMessage>.Update.Set(m => m.Tags, tags), cancellationToken: ct);
            foreach (var tag in tags.Where(WhatsAppIntel.Extractable.Contains))
                await WhatsAppAutomationModule.AddInsightAsync(db, new WhatsAppInsight
                {
                    ConnectionId = connection.Id, ChatId = stored.ChatId, ChatName = stored.ChatName, Kind = tag,
                    Text = stored.Text.Length <= 300 ? stored.Text : stored.Text[..300] + "…",
                    Value = WhatsAppIntel.ExtractValue(tag, stored.Text), CreatedAt = DateTime.UtcNow,
                }, ct);
            matchedRules = await WhatsAppAutomationModule.MatchRulesAsync(
                db, connection.Id, stored.ChatId, stored.IsGroup, stored.Text, tags, highPriority, !IntegrationsModule.IsWithinActiveHours(settings), ct);
        }

        var passesNotifyFilter = settings.NotifyScope != "Filtered" || highPriority
            || tags.Any(settings.NotifyTags.Contains)
            || (settings.NotifyTags.Contains("unknown-number") && !stored.IsGroup && !isSavedContact)
            || (settings.NotifyTags.Contains("priority-contact") && highPriority);
        var forceNotify = matchedRules.Any(r => r.Actions.Any(a => a.Type == "Notify"));
        var notify = isReceived && (forceNotify || (connection.NotificationsEnabled && passesMonitoring && passesNotifyFilter));

        await IntegrationEvents.PublishAsync(hub, connection.UserId, "TelegramMessageReceived", new
        {
            connectionId = connection.Id, chatId = stored.ChatId, chatName = stored.ChatName, isGroup = stored.IsGroup, fromMe = stored.FromMe,
            messageId = stored.MessageId, preview = settings.HideMessagePreviews ? null : stored.Text, timestamp = stored.Timestamp,
            notify, tags, priority = highPriority, sound = settings.NotificationSound, desktop = settings.DeliverDesktop,
        }, ct);

        if (!isReceived || !monitored) return Results.Ok();

        if (notify && settings.DeliverEmail)
        {
            try
            {
                var user = await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == connection.UserId).FirstOrDefaultAsync(ct);
                var body = settings.HideMessagePreviews ? "(message preview hidden)" : stored.Text;
                if (user is not null) await emailSender.SendAsync(user.Email, $"New Telegram message from {stored.ChatName ?? "a contact"}", $"{stored.ChatName ?? "A contact"} wrote on Telegram:\n\n{body}", ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Email notification failed for connection {ConnectionId}", connection.Id); }
        }

        // Everything below can take a while (AI calls, sends), so it runs off the request.
        _ = Task.Run(() => TelegramAutoReply.ProcessAsync(scopes, connection, stored, chatRule, matchedRules, logger), CancellationToken.None);
        return Results.Ok();
    }
}

/// <summary>Automatic replies for Telegram, honouring the same settings as WhatsApp: pause, schedule, scope, per-chat
/// rules, custom and out-of-hours messages, delay, cooldown, burst batching, approval and AI behaviour. Stays
/// silent if you answered yourself in the meantime.</summary>
public static class TelegramAutoReply
{
    private static readonly ConcurrentDictionary<string, long> BatchTickets = new();

    /// <summary>Runs the matched automation rules, then the connection-wide auto-reply unless a rule answered.</summary>
    public static async Task ProcessAsync(IServiceScopeFactory scopes, IntegrationConnection connection, TelegramMessage message,
        WhatsAppChatRule? chatRule, List<WhatsAppAutomationRule> matchedRules, ILogger logger)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IMongoDatabase>();
            var settings = await WhatsAppSettingsModule.GetOrCreateAsync(db, connection.Id, CancellationToken.None);
            var repliedByRule = false;
            if (matchedRules.Count > 0)
            {
                var userContext = new UserAiContext(connection.UserId, MemoryEnabled: false, AllowProviderDataSharing: true);
                await WhatsAppAutomationModule.RunStoreActionsAsync(db, sp.GetRequiredService<IAssistantOrchestrator>(), userContext, matchedRules,
                    connection.Id, message.ChatId, message.ChatName, message.Text, logger, CancellationToken.None);
                foreach (var action in matchedRules.SelectMany(r => r.Actions))
                {
                    if (action.Type == "AiReply") { await RunAsync(scopes, connection, message, chatRule, force: true, logger); repliedByRule = true; }
                    else if (action.Type == "FixedReply" && !settings.Paused && !string.IsNullOrWhiteSpace(action.Text))
                    {
                        try { await TelegramIntegrationModule.SendAndStoreAsync(db, sp.GetRequiredService<TelegramIntegrationProvider>(), connection.Id, message.ChatId, action.Text, false, CancellationToken.None); repliedByRule = true; }
                        catch (Exception ex) { logger.LogWarning(ex, "Automation rule fixed reply failed for connection {ConnectionId}", connection.Id); }
                    }
                }
            }

            var allowed = chatRule?.AutoReply switch
            {
                "Always" => true,
                "Never" => false,
                _ => (connection.AutoReplyEnabled || BusyMode.IsBusy(await BusyMode.LoadAsync(db, connection.UserId), DateTime.UtcNow)) && (!message.IsGroup || connection.AutoReplyInGroups),
            };
            if (!repliedByRule && allowed) await RunAsync(scopes, connection, message, chatRule, force: false, logger);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Telegram message handling failed for connection {ConnectionId}", connection.Id); }
    }

    /// <summary>Adds the "Reply by AI Assistant" line unless the user switched it off.</summary>
    public static async Task<string> WithSignatureAsync(IMongoDatabase db, string connectionId, string text, CancellationToken ct)
    {
        var settings = await db.GetCollection<WhatsAppSettings>("whatsappSettings").Find(s => s.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
        if (!(settings?.AiSignatureEnabled ?? true)) return text;
        var signature = string.IsNullOrWhiteSpace(settings?.AiSignatureText) ? WhatsAppSettings.DefaultAiSignature : settings!.AiSignatureText!.Trim();
        return $"{text}\n\n{signature}";
    }

    /// <summary>The "my boss will get back to you" reply, written by the AI like a person. Telegram contacts aren't known as saved, so the AI introduces itself once.</summary>
    private static async Task<string> ComposeHumanHoldingAsync(IServiceProvider sp, WhatsAppSettings settings, IntegrationConnection connection, string chatId, string? chatName, string text, GateOutcome gate,
        string? busyNote, IMongoCollection<TelegramMessage> messages, ILogger logger)
    {
        var fallback = gate.Text!;
        try
        {
            List<(string, string)>? history = null;
            if (settings.AllowMessageContentAsAiContext && settings.UseConversationContext && settings.ContextRetentionMessages > 0)
            {
                var recent = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId).SortByDescending(m => m.Timestamp).Limit(settings.ContextRetentionMessages).ToListAsync();
                recent.Reverse();
                history = recent.Select(m => (m.FromMe ? "assistant" : "user", m.Text)).ToList();
            }
            var prompt = HumanReply.BuildPrompt(settings, gate.Verdict, savedContact: false, HumanReply.AlreadyIntroduced(history), chatName ?? "", text, busyNote);
            var result = await sp.GetRequiredService<IAssistantOrchestrator>().RespondAsync(new AssistantRequest(new UserAiContext(connection.UserId, MemoryEnabled: false, AllowProviderDataSharing: true), prompt, history));
            return result.IsSimulated ? fallback : HumanReply.Clean(result.Text) ?? fallback;
        }
        catch (Exception ex) { logger.LogInformation(ex, "Human-style holding reply failed; using the template for {ChatId}", chatId); return fallback; }
    }

    private static async Task RunAsync(IServiceScopeFactory scopes, IntegrationConnection connection, TelegramMessage trigger, WhatsAppChatRule? chatRule, bool force, ILogger logger)
    {
        try
        {
            var receivedAt = DateTime.UtcNow.AddSeconds(-2);
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IMongoDatabase>();
            var settings = await WhatsAppSettingsModule.GetOrCreateAsync(db, connection.Id, CancellationToken.None);
            var chatId = trigger.ChatId;
            var messages = db.GetCollection<TelegramMessage>("telegramMessages");

            // `force` (an automation rule asked for it) bypasses scope, hours and cooldown, never pause or "Never".
            if (settings.Paused) return;
            if (settings.AutoReplyBlockedChats.Contains(chatId) || chatRule?.AutoReply == "Never") return;
            var forcedIn = force || chatRule?.AutoReply == "Always";
            if (!forcedIn && settings.AutoReplyScope == "SelectedOnly" && !settings.AutoReplyAllowedChats.Contains(chatId)) return;

            var busyProfile = await BusyMode.LoadAsync(db, connection.UserId);
            var busy = BusyMode.IsBusy(busyProfile, DateTime.UtcNow); // busy mode (profile-wide) overrides the active-hours rules
            var withinHours = busy || IntegrationsModule.IsWithinActiveHours(settings);
            var useOutsideHoursMessage = !busy && !force && !withinHours && !string.IsNullOrWhiteSpace(settings.OutsideHoursMessage);
            if (!force && !settings.OperateContinuously && !withinHours && !useOutsideHoursMessage) return;

            if (!forcedIn && settings.AutoReplyScope == "UnknownOnly"
                && await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe).AnyAsync()) return;

            var stateCollection = db.GetCollection<WhatsAppAutoReplyState>("whatsappAutoReplyState");
            if (!force && settings.CooldownSeconds > 0)
            {
                var state = await stateCollection.Find(s => s.ConnectionId == connection.Id && s.ChatId == chatId).FirstOrDefaultAsync();
                if (state is not null && (DateTime.UtcNow - state.LastRepliedAt).TotalSeconds < settings.CooldownSeconds) return;
            }

            // Several messages in a row → one reply: each takes a ticket and waits out the window; only the newest carries on.
            var key = $"{connection.Id}:{chatId}";
            var ticket = BatchTickets.AddOrUpdate(key, 1, (_, t) => t + 1);
            if (settings.BatchWindowSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.BatchWindowSeconds));
                if (BatchTickets.TryGetValue(key, out var latest) && latest != ticket) return;
            }

            var lastReply = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe).SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync();
            var pending = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && !m.FromMe && m.Timestamp > (lastReply != null ? lastReply.Timestamp : DateTime.MinValue))
                .SortByDescending(m => m.Timestamp).Limit(10).ToListAsync();
            pending.Reverse();
            var text = pending.Count > 0
                ? string.Join("\n", pending.Select(m => trigger.IsGroup && m.SenderName is { Length: > 0 } n ? $"{n}: {m.Text}" : m.Text))
                : trigger.Text;

            string replyText;
            var isAi = false;
            if (!force && !string.IsNullOrWhiteSpace(chatRule?.CustomReply)) replyText = chatRule.CustomReply;
            else if (useOutsideHoursMessage) replyText = settings.OutsideHoursMessage!;
            else if (busy && !force && BusyMode.FixedReply(busyProfile) is { } busyFixed) replyText = busyFixed;
            else if (settings.CustomMessageEnabled && !force)
            {
                if (string.IsNullOrWhiteSpace(settings.CustomMessage)) return;
                replyText = settings.CustomMessage;
            }
            else
            {
                if (settings.AiExcludedChats.Contains(chatId) || chatRule?.AiExcluded == true) return;

                // The rule gate (see ReplyGate): the AI only answers simple social messages; everything else is held for the boss.
                var gate = await ReplyGateFlow.ResolveAsync(db, settings, connection.Id, chatId, trigger.ChatName, text, trigger.IsGroup,
                    since => messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp > since).AnyAsync(),
                    DateTime.UtcNow, forced: null);
                logger.LogInformation("Telegram auto-reply for {ChatId}: gate says {Action} ({Reason})", chatId, gate.Action, gate.Reason);
                if (gate.Action == GateAction.Silent) { logger.LogInformation("Reply gate: no Telegram reply for {ChatId} ({Reason})", chatId, gate.Reason); return; }
                if (gate.Action == GateAction.SendHolding)
                {
                    replyText = await ComposeHumanHoldingAsync(sp, settings, connection, chatId, trigger.ChatName, text, gate, BusyMode.ActiveNote(busyProfile, DateTime.UtcNow), messages, logger);
                    isAi = true; goto Deliver;
                }

                List<(string, string)>? history = null;
                if (settings.AllowMessageContentAsAiContext && settings.UseConversationContext && settings.ContextRetentionMessages > 0)
                {
                    var recent = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId).SortByDescending(m => m.Timestamp).Limit(settings.ContextRetentionMessages).ToListAsync();
                    recent.Reverse();
                    history = recent.Select(m => (m.FromMe ? "assistant" : "user", m.Text)).ToList();
                }
                var userContext = new UserAiContext(connection.UserId, MemoryEnabled: false, AllowProviderDataSharing: true);
                var prompt = IntegrationsModule.BuildAutoReplyPrompt(settings, chatId, trigger.ChatName, text, trigger.IsGroup, null, "Telegram", BusyMode.ActiveNote(busyProfile, DateTime.UtcNow));
                var result = await sp.GetRequiredService<IAssistantOrchestrator>().RespondAsync(new AssistantRequest(userContext, prompt, history));
                if (result.IsSimulated || string.IsNullOrWhiteSpace(result.Text)) { logger.LogWarning("Telegram auto-reply for {ChatId}: the AI gave no usable answer", chatId); return; }
                if (result.Text.Trim().Equals("NO_REPLY", StringComparison.OrdinalIgnoreCase)) return;
                if (ReplyGate.ModelSaidNeedBoss(result.Text))
                {
                    var hold = await ReplyGateFlow.ResolveAsync(db, settings, connection.Id, chatId, trigger.ChatName, text, trigger.IsGroup,
                        since => messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp > since).AnyAsync(),
                        DateTime.UtcNow, GateVerdict.Boss);
                    if (hold.Action != GateAction.SendHolding) return;
                    replyText = await ComposeHumanHoldingAsync(sp, settings, connection, chatId, trigger.ChatName, text, hold, BusyMode.ActiveNote(busyProfile, DateTime.UtcNow), messages, logger);
                }
                else replyText = result.Text.Trim();
                isAi = true;
            }

            Deliver:
            if (settings.ResponseDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(settings.ResponseDelaySeconds));

            // If you answered yourself while this was being prepared or delayed, don't talk over you.
            if (await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp >= receivedAt).AnyAsync())
            {
                logger.LogInformation("Telegram auto-reply for {ChatId} skipped: the owner answered in the meantime", chatId);
                return;
            }

            if (settings.RequireApproval)
            {
                await db.GetCollection<WhatsAppPendingReply>("whatsappPendingReplies").InsertOneAsync(new WhatsAppPendingReply
                {
                    ConnectionId = connection.Id, ChatId = chatId, ChatName = trigger.ChatName, Text = replyText, CreatedAt = DateTime.UtcNow,
                });
                return;
            }

            var wire = isAi ? await WithSignatureAsync(db, connection.Id, replyText, CancellationToken.None) : replyText;
            logger.LogInformation("Telegram auto-reply for {ChatId}: sending", chatId);
            await TelegramIntegrationModule.SendAndStoreAsync(db, sp.GetRequiredService<TelegramIntegrationProvider>(), connection.Id, chatId, wire, isAiGenerated: true, CancellationToken.None);
            await stateCollection.UpdateOneAsync(s => s.ConnectionId == connection.Id && s.ChatId == chatId,
                Builders<WhatsAppAutoReplyState>.Update.Set(s => s.LastRepliedAt, DateTime.UtcNow).SetOnInsert(s => s.ConnectionId, connection.Id).SetOnInsert(s => s.ChatId, chatId),
                new UpdateOptions { IsUpsert = true });
        }
        catch (Exception ex) { logger.LogWarning(ex, "Telegram auto-reply failed for connection {ConnectionId}", connection.Id); }
    }
}
