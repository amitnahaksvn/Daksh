using Daksh.Api.Auth;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>
/// Extended, per-connection WhatsApp configuration beyond the basic on/off flags already on
/// <see cref="IntegrationConnection"/> (NotificationsEnabled, AutoReplyEnabled, AutoReplyInGroups — those stay
/// there since they're already wired into real enforcement). One document per ConnectionId.
/// </summary>
public sealed class WhatsAppSettings
{
    [BsonId] public string ConnectionId { get; set; } = null!;

    // General
    public bool Paused { get; set; }
    public bool OperateContinuously { get; set; } = true;
    public string ActiveHoursStart { get; set; } = "09:00";
    public string ActiveHoursEnd { get; set; } = "18:00";
    public string Timezone { get; set; } = "UTC";
    /// <summary>Days of the week (0 = Sunday … 6 = Saturday) the active-hours window applies to. Auto-reply is
    /// skipped (or answered with the out-of-hours message) on any other day. Defaults to every day.</summary>
    public List<int> ActiveDays { get; set; } = [0, 1, 2, 3, 4, 5, 6];
    /// <summary>Separate from IntegrationConnection.NotificationsEnabled (incoming) — stored for now but not
    /// yet wired into a live push; see docs/integrations/whatsapp/limitations.md.</summary>
    public bool OutgoingNotificationsEnabled { get; set; }

    // Monitoring — gates whether a message category is even reported as a notification/considered for
    // auto-reply; it's never withheld from being stored (the chat thread always shows everything received).
    public bool MonitorPersonal { get; set; } = true;
    public bool MonitorGroups { get; set; } = true;
    public bool MonitorImages { get; set; } = true;
    public bool MonitorDocuments { get; set; } = true;
    public bool MonitorAudio { get; set; } = true;
    public bool MonitorVideos { get; set; } = true;
    public bool MonitorLinks { get; set; } = true;
    public bool MonitorLocation { get; set; } = true;
    public bool MonitorCalls { get; set; } = true;
    public bool MonitorSavedContacts { get; set; } = true;
    public bool MonitorUnknownNumbers { get; set; } = true;
    public bool MonitorBusiness { get; set; } = true;

    // Auto Reply (extended — Enabled/InGroups themselves live on IntegrationConnection)
    public string AutoReplyScope { get; set; } = "All"; // "All" | "UnknownOnly" | "SelectedOnly"
    /// <summary>Chat ids (contacts and groups) auto-reply is limited to when the scope is "SelectedOnly".</summary>
    public List<string> AutoReplyAllowedChats { get; set; } = [];
    /// <summary>Chat ids auto-reply never answers, whatever the scope.</summary>
    public List<string> AutoReplyBlockedChats { get; set; } = [];
    /// <summary>Sent as the reply (instead of the AI or the normal custom message) when a message arrives
    /// outside the active-hours window.</summary>
    public string? OutsideHoursMessage { get; set; }
    /// <summary>Fixed reply used instead of the AI for WhatsApp Business contacts.</summary>
    public string? BusinessMessage { get; set; }
    public bool CustomMessageEnabled { get; set; }
    public string? CustomMessage { get; set; }
    public int ResponseDelaySeconds { get; set; }
    public int CooldownSeconds { get; set; }
    /// <summary>When several messages arrive in a chat within this many seconds of each other, wait for them to
    /// stop and send one reply covering all of them. 0 = reply to every message separately.</summary>
    public int BatchWindowSeconds { get; set; } = 5;
    public bool RequireApproval { get; set; }

    // AI Behaviour
    public string Personality { get; set; } = "Friendly"; // Professional | Friendly | Casual | Formal | Custom
    public string? CustomPersonality { get; set; }
    public string ResponseLength { get; set; } = "Balanced"; // Short | Balanced | Detailed
    public string? CustomInstructions { get; set; }
    public string? NeverRespondTopics { get; set; }
    /// <summary>The rule gate that runs before any AI reply: the AI only answers greetings and pleasantries; anything needing a decision is held
    /// for the owner and the sender gets a holding reply. On by default — turning it off lets the AI answer everything, as it used to.</summary>
    public bool ReplyGateEnabled { get; set; } = true;
    /// <summary>How the assistant refers to the owner in holding replies ("my boss", "Amit sir"…).</summary>
    public string BossReference { get; set; } = "my boss";
    /// <summary>At most one holding reply per chat in this many minutes, unless the owner has answered since (0 = reply every time).</summary>
    public int GateHoldingCooldownMinutes { get; set; } = 240;
    public bool UseConversationContext { get; set; } = true;
    public int ContextRetentionMessages { get; set; } = 5;
    public bool ReplyInSenderLanguage { get; set; }
    public bool SummarizeLongConversations { get; set; }
    /// <summary>Append a visible "this was an AI reply" line to every AI-generated WhatsApp reply.</summary>
    public const string DefaultAiSignature = "🤖 Reply by AI Assistant";
    public bool AiSignatureEnabled { get; set; } = true;
    public string? AiSignatureText { get; set; }

    // Notifications
    public string NotifyScope { get; set; } = "All"; // "All" | "Filtered"
    /// <summary>With scope "Filtered": notify only for messages carrying one of these tags (question, deadline,
    /// payment, meeting, otp, suspicious-link, attachment, unknown-number, priority-contact).</summary>
    public List<string> NotifyTags { get; set; } = ["question", "deadline", "payment", "meeting", "otp", "suspicious-link"];
    public bool DeliverEmail { get; set; }
    public bool DeliverWhatsAppSelf { get; set; }
    public bool DeliverDesktop { get; set; }
    public bool NotificationSound { get; set; } = true;

    // Message Intelligence — detection categories switched off here are neither tagged nor extracted.
    public List<string> DisabledIntelligenceTags { get; set; } = [];

    // Privacy & Security
    /// <summary>Chats Daksha never runs AI (auto-reply) on.</summary>
    public List<string> AiExcludedChats { get; set; } = [];
    public bool HideMessagePreviews { get; set; }
    /// <summary>0 = keep forever. Enforced only via the manual "purge now" action today — no background
    /// job walks this automatically yet; see docs/integrations/whatsapp/limitations.md.</summary>
    public int MessageRetentionDays { get; set; }
    public bool AllowMessageContentAsAiContext { get; set; } = true;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Per-(connection, chat) cooldown tracking for auto-reply, so "don't reply again within N seconds"
/// survives across separate incoming messages rather than needing in-memory state per process.</summary>
public sealed class WhatsAppAutoReplyState
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string ChatId { get; set; }
    public DateTime LastRepliedAt { get; set; }
    /// <summary>When the last "I'll check with my boss" holding reply went to this chat.</summary>
    public DateTime? LastHoldingAt { get; set; }
}

/// <summary>An AI-drafted reply held for the user's explicit approval (RequireApproval) instead of being
/// sent automatically.</summary>
public sealed class WhatsAppPendingReply
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string ChatId { get; set; }
    public string? ChatName { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record GeneralSettingsDto(bool Paused, bool OperateContinuously, string ActiveHoursStart, string ActiveHoursEnd, string Timezone, IReadOnlyList<int> ActiveDays, bool IncomingNotificationsEnabled, bool OutgoingNotificationsEnabled);
public sealed record MonitoringSettingsDto(bool MonitorPersonal, bool MonitorGroups, bool MonitorImages, bool MonitorDocuments, bool MonitorAudio, bool MonitorVideos, bool MonitorLinks, bool MonitorLocation, bool MonitorCalls, bool MonitorSavedContacts, bool MonitorUnknownNumbers, bool MonitorBusiness);
public sealed record AutoReplySettingsDto(bool Enabled, bool InGroups, string Scope, bool CustomMessageEnabled, string? CustomMessage, int ResponseDelaySeconds, int CooldownSeconds, bool RequireApproval, IReadOnlyList<string> AllowedChats, IReadOnlyList<string> BlockedChats, string? OutsideHoursMessage, string? BusinessMessage, int BatchWindowSeconds, bool ReplyGateEnabled = true, string BossReference = "my boss", int HoldingCooldownMinutes = 240);
public sealed record AiBehaviourSettingsDto(string Personality, string? CustomPersonality, string ResponseLength, string? CustomInstructions, string? NeverRespondTopics, bool UseConversationContext, int ContextRetentionMessages, bool ReplyInSenderLanguage, bool SummarizeLongConversations, bool AiSignatureEnabled, string? AiSignatureText);
public sealed record PrivacySectionDto(bool HideMessagePreviews, int MessageRetentionDays, bool AllowMessageContentAsAiContext, IReadOnlyList<string> AiExcludedChats);
public sealed record NotificationSettingsDto(string Scope, IReadOnlyList<string> Tags, bool Email, bool WhatsAppSelf, bool Desktop, bool Sound);
public sealed record IntelligenceSettingsDto(IReadOnlyList<string> DisabledTags);
public sealed record WhatsAppFullSettingsDto(string ConnectionId, string AccountIdentifier, string Status, GeneralSettingsDto General, MonitoringSettingsDto Monitoring, AutoReplySettingsDto AutoReply, AiBehaviourSettingsDto AiBehaviour, PrivacySectionDto Privacy, NotificationSettingsDto Notifications, IntelligenceSettingsDto Intelligence);

public sealed record UpdateGeneralSettingsRequest(bool? Paused, bool? OperateContinuously, string? ActiveHoursStart, string? ActiveHoursEnd, string? Timezone, List<int>? ActiveDays, bool? IncomingNotificationsEnabled, bool? OutgoingNotificationsEnabled);
public sealed record UpdateMonitoringSettingsRequest(bool? MonitorPersonal, bool? MonitorGroups, bool? MonitorImages, bool? MonitorDocuments, bool? MonitorAudio, bool? MonitorVideos, bool? MonitorLinks, bool? MonitorLocation, bool? MonitorCalls, bool? MonitorSavedContacts, bool? MonitorUnknownNumbers, bool? MonitorBusiness);
public sealed record UpdateAutoReplySettingsRequest(bool? Enabled, bool? InGroups, string? Scope, bool? CustomMessageEnabled, string? CustomMessage, int? ResponseDelaySeconds, int? CooldownSeconds, bool? RequireApproval, List<string>? AllowedChats, List<string>? BlockedChats, string? OutsideHoursMessage, string? BusinessMessage, int? BatchWindowSeconds = null, bool? ReplyGateEnabled = null, string? BossReference = null, int? HoldingCooldownMinutes = null);
public sealed record UpdateAiBehaviourSettingsRequest(string? Personality, string? CustomPersonality, string? ResponseLength, string? CustomInstructions, string? NeverRespondTopics, bool? UseConversationContext, int? ContextRetentionMessages, bool? ReplyInSenderLanguage, bool? SummarizeLongConversations, bool? AiSignatureEnabled, string? AiSignatureText);
public sealed record UpdatePrivacySectionRequest(bool? HideMessagePreviews, int? MessageRetentionDays, bool? AllowMessageContentAsAiContext, List<string>? AiExcludedChats);
public sealed record UpdateNotificationSettingsRequest(string? Scope, List<string>? Tags, bool? Email, bool? WhatsAppSelf, bool? Desktop, bool? Sound);
public sealed record UpdateIntelligenceSettingsRequest(List<string>? DisabledTags);
public sealed record UpdateWhatsAppFullSettingsRequest(UpdateGeneralSettingsRequest? General, UpdateMonitoringSettingsRequest? Monitoring, UpdateAutoReplySettingsRequest? AutoReply, UpdateAiBehaviourSettingsRequest? AiBehaviour, UpdatePrivacySectionRequest? Privacy, UpdateNotificationSettingsRequest? Notifications = null, UpdateIntelligenceSettingsRequest? Intelligence = null);
public sealed record PendingReplyDto(string Id, string ChatId, string? ChatName, string Text, DateTime CreatedAt);

/// <summary>Classifies an incoming message against the Monitoring settings — gates whether it's reported as
/// a notification or considered for auto-reply, never whether it's stored (the thread always shows everything).</summary>
public static class WhatsAppMonitoring
{
    public static bool Passes(WhatsAppSettings s, bool isGroup, string text, bool isSavedContact = false, bool isBusiness = false)
    {
        if (!isGroup && isBusiness && !s.MonitorBusiness) return false;
        if (!isGroup && isSavedContact && !s.MonitorSavedContacts) return false;
        if (!isGroup && !isSavedContact && !s.MonitorUnknownNumbers) return false;
        if ((text.StartsWith("📞", StringComparison.Ordinal) || text.StartsWith("📹", StringComparison.Ordinal)) && !s.MonitorCalls) return false;
        if (isGroup && !s.MonitorGroups) return false;
        if (!isGroup && !s.MonitorPersonal) return false;
        if (text.StartsWith("📷", StringComparison.Ordinal) && !s.MonitorImages) return false;
        if (text.StartsWith("🎥", StringComparison.Ordinal) && !s.MonitorVideos) return false;
        if (text.StartsWith("🎤", StringComparison.Ordinal) && !s.MonitorAudio) return false;
        if (text.StartsWith("📄", StringComparison.Ordinal) && !s.MonitorDocuments) return false;
        if (text.StartsWith("📍", StringComparison.Ordinal) && !s.MonitorLocation) return false;
        if (!s.MonitorLinks && (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}

public static class WhatsAppSettingsModule
{
    public static void MapWhatsAppSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/whatsapp").WithTags("WhatsAppSettings").RequireAuthorization(AuthSetup.UserScheme);
        g.MapGet("/{connectionId}/full-settings", GetFullSettingsAsync);
        g.MapPatch("/{connectionId}/full-settings", UpdateFullSettingsAsync);
        g.MapGet("/{connectionId}/pending-replies", ListPendingRepliesAsync);
        g.MapPost("/{connectionId}/pending-replies/{replyId}/approve", ApprovePendingReplyAsync);
        g.MapDelete("/{connectionId}/pending-replies/{replyId}", DiscardPendingReplyAsync);
        g.MapPost("/{connectionId}/purge-messages", PurgeMessagesAsync);
    }

    public static async Task EnsureIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        var autoReplyState = db.GetCollection<WhatsAppAutoReplyState>("whatsappAutoReplyState");
        await autoReplyState.Indexes.CreateOneAsync(
            new CreateIndexModel<WhatsAppAutoReplyState>(
                Builders<WhatsAppAutoReplyState>.IndexKeys.Ascending(s => s.ConnectionId).Ascending(s => s.ChatId),
                new CreateIndexOptions { Unique = true, Name = "ux_whatsappAutoReplyState_connection_chat" }),
            cancellationToken: ct);
    }

    public static async Task<WhatsAppSettings> GetOrCreateAsync(IMongoDatabase db, string connectionId, CancellationToken ct)
    {
        var collection = db.GetCollection<WhatsAppSettings>("whatsappSettings");
        var existing = await collection.Find(s => s.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var fresh = new WhatsAppSettings { ConnectionId = connectionId, UpdatedAt = DateTime.UtcNow };
        try
        {
            await collection.InsertOneAsync(fresh, cancellationToken: ct);
        }
        catch (MongoWriteException)
        {
            // Lost a race with a concurrent GetOrCreateAsync — the other insert won, read what it wrote.
            return await collection.Find(s => s.ConnectionId == connectionId).FirstOrDefaultAsync(ct) ?? fresh;
        }
        return fresh;
    }

    private static async Task<IResult> GetFullSettingsAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        var s = await GetOrCreateAsync(db, connectionId, ct);
        return Results.Ok(ToFullDto(s, connection));
    }

    private static async Task<IResult> UpdateFullSettingsAsync(string connectionId, UpdateWhatsAppFullSettingsRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        await GetOrCreateAsync(db, connectionId, ct); // ensure a document exists before the targeted update below
        var settings = db.GetCollection<WhatsAppSettings>("whatsappSettings");
        var update = Builders<WhatsAppSettings>.Update.Set(s => s.UpdatedAt, DateTime.UtcNow);
        var changed = false;

        if (req.General is { } general)
        {
            if (general.Paused is { } v) { update = update.Set(s => s.Paused, v); changed = true; }
            if (general.OperateContinuously is { } v2) { update = update.Set(s => s.OperateContinuously, v2); changed = true; }
            if (general.ActiveHoursStart is { } v3) { update = update.Set(s => s.ActiveHoursStart, v3); changed = true; }
            if (general.ActiveHoursEnd is { } v4) { update = update.Set(s => s.ActiveHoursEnd, v4); changed = true; }
            if (general.ActiveDays is { } days) { update = update.Set(s => s.ActiveDays, days.Where(d => d is >= 0 and <= 6).Distinct().Order().ToList()); changed = true; }
            if (general.Timezone is { } v5) { update = update.Set(s => s.Timezone, v5); changed = true; }
            if (general.OutgoingNotificationsEnabled is { } v5b) { update = update.Set(s => s.OutgoingNotificationsEnabled, v5b); changed = true; }
            if (general.IncomingNotificationsEnabled is { } v6)
                await connections.UpdateOneAsync(c => c.Id == connectionId, Builders<IntegrationConnection>.Update.Set(c => c.NotificationsEnabled, v6), cancellationToken: ct);
        }
        if (req.Monitoring is { } monitoring)
        {
            if (monitoring.MonitorPersonal is { } v) { update = update.Set(s => s.MonitorPersonal, v); changed = true; }
            if (monitoring.MonitorGroups is { } v2) { update = update.Set(s => s.MonitorGroups, v2); changed = true; }
            if (monitoring.MonitorImages is { } v3) { update = update.Set(s => s.MonitorImages, v3); changed = true; }
            if (monitoring.MonitorDocuments is { } v4) { update = update.Set(s => s.MonitorDocuments, v4); changed = true; }
            if (monitoring.MonitorAudio is { } v5) { update = update.Set(s => s.MonitorAudio, v5); changed = true; }
            if (monitoring.MonitorVideos is { } v6) { update = update.Set(s => s.MonitorVideos, v6); changed = true; }
            if (monitoring.MonitorLinks is { } v7) { update = update.Set(s => s.MonitorLinks, v7); changed = true; }
            if (monitoring.MonitorLocation is { } v8) { update = update.Set(s => s.MonitorLocation, v8); changed = true; }
            if (monitoring.MonitorCalls is { } v9) { update = update.Set(s => s.MonitorCalls, v9); changed = true; }
            if (monitoring.MonitorSavedContacts is { } v10) { update = update.Set(s => s.MonitorSavedContacts, v10); changed = true; }
            if (monitoring.MonitorUnknownNumbers is { } v11) { update = update.Set(s => s.MonitorUnknownNumbers, v11); changed = true; }
            if (monitoring.MonitorBusiness is { } v12) { update = update.Set(s => s.MonitorBusiness, v12); changed = true; }
        }
        if (req.AutoReply is { } autoReply)
        {
            if (autoReply.Scope is { } v) { update = update.Set(s => s.AutoReplyScope, v); changed = true; }
            if (autoReply.CustomMessageEnabled is { } v2) { update = update.Set(s => s.CustomMessageEnabled, v2); changed = true; }
            if (autoReply.CustomMessage is { } v3) { update = update.Set(s => s.CustomMessage, v3); changed = true; }
            if (autoReply.ResponseDelaySeconds is { } v4) { update = update.Set(s => s.ResponseDelaySeconds, v4); changed = true; }
            if (autoReply.CooldownSeconds is { } v5) { update = update.Set(s => s.CooldownSeconds, v5); changed = true; }
            if (autoReply.BatchWindowSeconds is { } bw) { update = update.Set(s => s.BatchWindowSeconds, Math.Clamp(bw, 0, 60)); changed = true; }
            if (autoReply.ReplyGateEnabled is { } gate) { update = update.Set(s => s.ReplyGateEnabled, gate); changed = true; }
            if (autoReply.BossReference is { } boss) { update = update.Set(s => s.BossReference, ReplyGate.NormaliseBoss(boss)); changed = true; }
            if (autoReply.HoldingCooldownMinutes is { } hold) { update = update.Set(s => s.GateHoldingCooldownMinutes, Math.Clamp(hold, 0, 10_080)); changed = true; }
            if (autoReply.RequireApproval is { } v6) { update = update.Set(s => s.RequireApproval, v6); changed = true; }
            if (autoReply.AllowedChats is { } a1) { update = update.Set(s => s.AutoReplyAllowedChats, a1); changed = true; }
            if (autoReply.BlockedChats is { } a2) { update = update.Set(s => s.AutoReplyBlockedChats, a2); changed = true; }
            if (autoReply.OutsideHoursMessage is { } a3) { update = update.Set(s => s.OutsideHoursMessage, a3); changed = true; }
            if (autoReply.BusinessMessage is { } a4) { update = update.Set(s => s.BusinessMessage, a4); changed = true; }

            var connUpdate = Builders<IntegrationConnection>.Update.Set(c => c.UpdatedAt, DateTime.UtcNow);
            var connChanged = false;
            if (autoReply.Enabled is { } v7) { connUpdate = connUpdate.Set(c => c.AutoReplyEnabled, v7); connChanged = true; }
            if (autoReply.InGroups is { } v8) { connUpdate = connUpdate.Set(c => c.AutoReplyInGroups, v8); connChanged = true; }
            if (connChanged) await connections.UpdateOneAsync(c => c.Id == connectionId, connUpdate, cancellationToken: ct);
        }
        if (req.AiBehaviour is { } ai)
        {
            if (ai.Personality is { } v) { update = update.Set(s => s.Personality, v); changed = true; }
            if (ai.CustomPersonality is { } v2) { update = update.Set(s => s.CustomPersonality, v2); changed = true; }
            if (ai.ResponseLength is { } v3) { update = update.Set(s => s.ResponseLength, v3); changed = true; }
            if (ai.CustomInstructions is { } v4) { update = update.Set(s => s.CustomInstructions, v4); changed = true; }
            if (ai.NeverRespondTopics is { } v5) { update = update.Set(s => s.NeverRespondTopics, v5); changed = true; }
            if (ai.UseConversationContext is { } v6) { update = update.Set(s => s.UseConversationContext, v6); changed = true; }
            if (ai.ContextRetentionMessages is { } v7) { update = update.Set(s => s.ContextRetentionMessages, v7); changed = true; }
            if (ai.ReplyInSenderLanguage is { } v8) { update = update.Set(s => s.ReplyInSenderLanguage, v8); changed = true; }
            if (ai.SummarizeLongConversations is { } v9) { update = update.Set(s => s.SummarizeLongConversations, v9); changed = true; }
            if (ai.AiSignatureEnabled is { } v10) { update = update.Set(s => s.AiSignatureEnabled, v10); changed = true; }
            if (ai.AiSignatureText is { } v11) { update = update.Set(s => s.AiSignatureText, v11.Trim().Length == 0 ? null : v11.Trim()); changed = true; }
        }
        if (req.Privacy is { } privacy)
        {
            if (privacy.HideMessagePreviews is { } v) { update = update.Set(s => s.HideMessagePreviews, v); changed = true; }
            if (privacy.MessageRetentionDays is { } v2) { update = update.Set(s => s.MessageRetentionDays, v2); changed = true; }
            if (privacy.AllowMessageContentAsAiContext is { } v3) { update = update.Set(s => s.AllowMessageContentAsAiContext, v3); changed = true; }
            if (privacy.AiExcludedChats is { } v4) { update = update.Set(s => s.AiExcludedChats, v4); changed = true; }
        }

        if (req.Notifications is { } notifications)
        {
            if (notifications.Scope is { } n1) { update = update.Set(s => s.NotifyScope, n1); changed = true; }
            if (notifications.Tags is { } n2) { update = update.Set(s => s.NotifyTags, n2); changed = true; }
            if (notifications.Email is { } n3) { update = update.Set(s => s.DeliverEmail, n3); changed = true; }
            if (notifications.WhatsAppSelf is { } n4) { update = update.Set(s => s.DeliverWhatsAppSelf, n4); changed = true; }
            if (notifications.Desktop is { } n5) { update = update.Set(s => s.DeliverDesktop, n5); changed = true; }
            if (notifications.Sound is { } n6) { update = update.Set(s => s.NotificationSound, n6); changed = true; }
        }
        if (req.Intelligence is { DisabledTags: { } disabledTags }) { update = update.Set(s => s.DisabledIntelligenceTags, disabledTags); changed = true; }

        if (changed) await settings.UpdateOneAsync(s => s.ConnectionId == connectionId, update, cancellationToken: ct);

        var freshSettings = await GetOrCreateAsync(db, connectionId, ct);
        var freshConnection = await connections.Find(c => c.Id == connectionId).FirstOrDefaultAsync(ct);
        return Results.Ok(ToFullDto(freshSettings, freshConnection!));
    }

    private static async Task<IResult> ListPendingRepliesAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        var replies = await db.GetCollection<WhatsAppPendingReply>("whatsappPendingReplies")
            .Find(r => r.ConnectionId == connectionId).SortByDescending(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(replies.Select(r => new PendingReplyDto(r.Id, r.ChatId, r.ChatName, r.Text, r.CreatedAt)));
    }

    private static async Task<IResult> ApprovePendingReplyAsync(string connectionId, string replyId, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, TelegramIntegrationProvider telegram, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var pending = db.GetCollection<WhatsAppPendingReply>("whatsappPendingReplies");
        var reply = await pending.Find(r => r.Id == replyId && r.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
        if (reply is null) return Results.NotFound();

        try
        {
            if (connection.IntegrationKey == TelegramIntegrationModule.IntegrationKey)
            {
                var wire = await TelegramAutoReply.WithSignatureAsync(db, connectionId, reply.Text, ct);
                await TelegramIntegrationModule.SendAndStoreAsync(db, telegram, connectionId, reply.ChatId, wire, isAiGenerated: true, ct);
            }
            else
                await whatsApp.SendMessageAsync(connectionId, reply.ChatId, reply.Text, quotedMessageId: null, ct, isAiGenerated: true);
        }
        catch (Exception ex)
        {
            return Results.Problem(statusCode: 502, title: "Could not send the approved reply.", detail: ex.Message);
        }
        await pending.DeleteOneAsync(r => r.Id == replyId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DiscardPendingReplyAsync(string connectionId, string replyId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        await db.GetCollection<WhatsAppPendingReply>("whatsappPendingReplies").DeleteOneAsync(r => r.Id == replyId && r.ConnectionId == connectionId, ct);
        return Results.NoContent();
    }

    /// <summary>Immediate, explicit purge — the real action behind Privacy's "delete stored message content".
    /// Retention-day enforcement is otherwise manual today; see docs/integrations/whatsapp/limitations.md.</summary>
    private static async Task<IResult> PurgeMessagesAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        await db.GetCollection<WhatsAppMessage>("whatsappMessages").DeleteManyAsync(m => m.ConnectionId == connectionId, ct);
        await db.GetCollection<TelegramMessage>("telegramMessages").DeleteManyAsync(m => m.ConnectionId == connectionId, ct);
        return Results.NoContent();
    }

    private static Task<IntegrationConnection?> OwnedConnectionAsync(IMongoDatabase db, string connectionId, string userId, CancellationToken ct) =>
        db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.Id == connectionId && c.UserId == userId)
            .FirstOrDefaultAsync(ct)!;

    private static WhatsAppFullSettingsDto ToFullDto(WhatsAppSettings s, IntegrationConnection c) => new(
        c.Id, c.AccountIdentifier ?? "unknown", c.Status.ToString(),
        new GeneralSettingsDto(s.Paused, s.OperateContinuously, s.ActiveHoursStart, s.ActiveHoursEnd, s.Timezone, s.ActiveDays, c.NotificationsEnabled, s.OutgoingNotificationsEnabled),
        new MonitoringSettingsDto(s.MonitorPersonal, s.MonitorGroups, s.MonitorImages, s.MonitorDocuments, s.MonitorAudio, s.MonitorVideos, s.MonitorLinks, s.MonitorLocation, s.MonitorCalls, s.MonitorSavedContacts, s.MonitorUnknownNumbers, s.MonitorBusiness),
        new AutoReplySettingsDto(c.AutoReplyEnabled, c.AutoReplyInGroups, s.AutoReplyScope, s.CustomMessageEnabled, s.CustomMessage, s.ResponseDelaySeconds, s.CooldownSeconds, s.RequireApproval, s.AutoReplyAllowedChats, s.AutoReplyBlockedChats, s.OutsideHoursMessage, s.BusinessMessage, s.BatchWindowSeconds, s.ReplyGateEnabled, s.BossReference, s.GateHoldingCooldownMinutes),
        new AiBehaviourSettingsDto(s.Personality, s.CustomPersonality, s.ResponseLength, s.CustomInstructions, s.NeverRespondTopics, s.UseConversationContext, s.ContextRetentionMessages, s.ReplyInSenderLanguage, s.SummarizeLongConversations, s.AiSignatureEnabled, s.AiSignatureText),
        new PrivacySectionDto(s.HideMessagePreviews, s.MessageRetentionDays, s.AllowMessageContentAsAiContext, s.AiExcludedChats),
        new NotificationSettingsDto(s.NotifyScope, s.NotifyTags, s.DeliverEmail, s.DeliverWhatsAppSelf, s.DeliverDesktop, s.NotificationSound),
        new IntelligenceSettingsDto(s.DisabledIntelligenceTags));
}
