using System.Net.Http.Json;
using Daksh.Ai;
using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// Reusable integration catalogue + per-user connection lifecycle. WhatsApp and Gmail are live providers;
// every other catalogue entry remains disabled until its provider is implemented.

public enum ConnectionStatus { NotConnected, Connecting, Connected, Disconnected, Error }

public sealed class IntegrationCatalogEntry
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string IntegrationKey { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    /// <summary>react-icons/si export name the web app already uses (e.g. "SiWhatsapp"), or a monogram fallback.</summary>
    public required string Icon { get; set; }
    public required string Color { get; set; }
    public required string Category { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A user's connection to one integration. One active (non-Disconnected) row per (UserId, IntegrationKey) today.</summary>
public sealed class IntegrationConnection
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string IntegrationKey { get; set; }
    public string? DisplayName { get; set; }
    public ConnectionStatus Status { get; set; } = ConnectionStatus.NotConnected;
    public string? AccountIdentifier { get; set; }
    public string? ProviderAccountId { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Whether a new-message event for this connection should be pushed to the user over SignalR.</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>When true, an incoming 1:1 message gets an AI-generated reply sent back automatically.</summary>
    public bool AutoReplyEnabled { get; set; }
    /// <summary>Auto-reply is 1:1-only unless this is also on — replying automatically in a group is a distinct,
    /// explicit opt-in since everyone in the group sees it.</summary>
    public bool AutoReplyInGroups { get; set; }
    /// <summary>Set whenever the user fetches this connection's messages; messages after this are "unread".</summary>
    public DateTime? LastViewedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A WhatsApp message, written exclusively by the Node/Baileys service (see services/whatsapp/src/db.ts) into
/// the shared "whatsappMessages" collection — this API only ever reads it, to answer "what came in" for the
/// Manage panel and the dashboard widget, the same read-only relationship it has with IntegrationCredential.
/// </summary>
public sealed class WhatsAppMessage
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonElement("connectionId")] public required string ConnectionId { get; set; }
    [BsonElement("messageId")] public required string MessageId { get; set; }
    [BsonElement("chatId")] public required string ChatId { get; set; }
    [BsonElement("chatName")] public string? ChatName { get; set; }
    [BsonElement("participant")] public string? Participant { get; set; }
    [BsonElement("fromMe")] public bool FromMe { get; set; }
    [BsonElement("isAiGenerated")] public bool IsAiGenerated { get; set; }
    [BsonElement("text")] public required string Text { get; set; }
    /// <summary>True when the Node service stored the photo/sticker bytes in "whatsappMedia".</summary>
    [BsonElement("hasMedia")] public bool HasMedia { get; set; }
    /// <summary>image, sticker, video, audio or document: known even before the bytes are downloaded.</summary>
    [BsonElement("mediaKind")] public string? MediaKind { get; set; }
    [BsonElement("mimeType")] public string? MimeType { get; set; }
    [BsonElement("fileName")] public string? FileName { get; set; }
    [BsonElement("mediaSeconds")] public int? MediaSeconds { get; set; }
    [BsonElement("voiceNote")] public bool VoiceNote { get; set; }
    /// <summary>Outgoing only: 1 pending, 2 sent, 3 delivered, 4 read, 5 played.</summary>
    [BsonElement("status")] public int? Status { get; set; }
    [BsonElement("deleted")] public bool Deleted { get; set; }
    [BsonElement("edited")] public bool Edited { get; set; }
    /// <summary>"Delete for me": hidden in Daksha only.</summary>
    [BsonElement("hidden")] public bool Hidden { get; set; }
    [BsonElement("starred")] public bool Starred { get; set; }
    [BsonElement("quotedMessageId")] public string? QuotedMessageId { get; set; }
    [BsonElement("quotedText")] public string? QuotedText { get; set; }
    [BsonElement("reactions")] public List<WhatsAppReaction> Reactions { get; set; } = [];
    /// <summary>Detection tags (question, payment, ...) set by the API when the message arrives.</summary>
    [BsonElement("tags")] public List<string> Tags { get; set; } = [];
    [BsonElement("timestamp")] public DateTime Timestamp { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Name for a contact or group, written by the Node service into "whatsappContacts" — read-only here.</summary>
[BsonIgnoreExtraElements]
public sealed class WhatsAppContact
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonElement("connectionId")] public required string ConnectionId { get; set; }
    [BsonElement("contactId")] public required string ContactId { get; set; }
    [BsonElement("lid")] public string? Lid { get; set; }
    [BsonElement("phoneNumber")] public string? PhoneNumber { get; set; }
    [BsonElement("name")] public string? Name { get; set; }
    [BsonElement("notify")] public string? Notify { get; set; }
    [BsonElement("isBusiness")] public bool IsBusiness { get; set; }
}

public sealed class WhatsAppReaction
{
    [BsonElement("by")] public required string By { get; set; }
    [BsonElement("emoji")] public required string Emoji { get; set; }
}

/// <summary>
/// Encrypted session/auth material for a connection. One document per (ConnectionId, CredentialKey) pair so a
/// single key (e.g. one Baileys signal-key entry) can be updated without touching the rest of the session.
/// Written and decrypted exclusively by the owning provider service (the WhatsApp Node service, for "whatsapp"
/// credentials) using the shared AES key at Security:Encryption:Keys:{ActiveKeyId} — never read or exposed by
/// this API; it exists here purely as the documented shared schema.
/// </summary>
public sealed class IntegrationCredential
{
    // This API never writes these documents — the Node service does, in camelCase (see
    // services/whatsapp/src/authStateMongo.ts). The [BsonElement]s below exist so the index this API
    // creates (see IntegrationsModule's index setup) actually targets the fields Node writes, instead of
    // silently indexing PascalCase fields that never exist on any document.
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonElement("connectionId")] public required string ConnectionId { get; set; }
    [BsonElement("credentialKey")] public required string CredentialKey { get; set; }
    [BsonElement("encryptedValue")] public required string EncryptedValue { get; set; }
    [BsonElement("encryptionVersion")] public required string EncryptionVersion { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; }
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; }
}

public sealed class UserDashboardApp
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string ConnectionId { get; set; }
    public bool IsVisible { get; set; } = true;
    public int DisplayOrder { get; set; }
    /// <summary>Legacy width in a 3-column grid (1–3). Superseded by <see cref="Cols"/>; still read so saved layouts carry over.</summary>
    public int ColSpan { get; set; }
    /// <summary>Tile width in a 12-column grid (3–12). 0 = the card's own default.</summary>
    public int Cols { get; set; }
    /// <summary>Tile height in pixels. 0 = fit the content. A shorter tile scrolls inside itself.</summary>
    public int Height { get; set; }
    /// <summary>The column (1–10) the tile starts in, in the 12-column grid. 0 = flow naturally after the previous tile.</summary>
    public int Col { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record IntegrationCatalogDto(string IntegrationKey, string Name, string Description, string Icon, string Color, string Category, bool IsEnabled);
public sealed record ConnectionStatusDto(string ConnectionId, string IntegrationKey, string Status, string? AccountIdentifier, DateTime? ConnectedAt, DateTime? LastActiveAt, string? LastError, bool NotificationsEnabled, bool AutoReplyEnabled, bool AutoReplyInGroups);
public sealed record IntegrationDetailDto(IntegrationCatalogDto Catalog, ConnectionStatusDto? Connection);
public sealed record QrResponse(string? Qr, DateTime? ExpiresAt, string Status);
public sealed record ConnectResponse(string ConnectionId, string Status);
public sealed record WhatsAppReactionDto(string By, string Emoji);
public sealed record WhatsAppMessageDto(
    string MessageId, string ChatId, string? ChatName, bool FromMe, bool IsAiGenerated, bool IsGroup, string Text,
    string? QuotedMessageId, string? QuotedText, IReadOnlyList<WhatsAppReactionDto> Reactions, DateTime Timestamp, bool HasMedia = false,
    string? MediaKind = null, string? MimeType = null, string? FileName = null, int? MediaSeconds = null, bool VoiceNote = false,
    int? Status = null, bool Deleted = false, bool Edited = false, bool Starred = false);
public sealed record WhatsAppChatDto(string ChatId, string? ChatName, bool IsGroup, string LastMessagePreview, DateTime LastMessageAt, int UnreadCount,
    bool Pinned = false, bool Archived = false, DateTime? MutedUntil = null);
public sealed record SendWhatsAppMessageRequest(string Text, string? QuotedMessageId = null);
public sealed record SendNewWhatsAppMessageRequest(string To, string Text);
public sealed record ReactToWhatsAppMessageRequest(string Emoji);
public sealed record UpdateConnectionSettingsRequest(bool? NotificationsEnabled, bool? AutoReplyEnabled, bool? AutoReplyInGroups);

/// <summary>A WhatsApp chat JID identifies a group iff it ends in "@g.us" — individuals end in "@s.whatsapp.net"
/// (or newer "@lid" JIDs). Centralised here so every endpoint and the auto-reply gate agree on it.</summary>
public static class WhatsAppChat
{
    public static bool IsGroup(string chatId) => chatId.EndsWith("@g.us", StringComparison.Ordinal);
}

/// <summary>
/// The per-provider operations the generic catalogue/connection endpoints delegate to. WhatsApp is the only
/// implementation today; a future Telegram/Slack/etc. provider plugs in the same way without the generic
/// endpoints changing. Deliberately NOT modeling providers this app doesn't build yet (no speculative members).
/// </summary>
public interface IIntegrationProvider
{
    string IntegrationKey { get; }
    Task ConnectAsync(string connectionId, CancellationToken ct);
    Task<QrResponse> GetQrAsync(string connectionId, CancellationToken ct);
    Task<ConnectionStatus> GetStatusAsync(string connectionId, CancellationToken ct);
    Task ReconnectAsync(string connectionId, CancellationToken ct);
    Task DisconnectAsync(string connectionId, CancellationToken ct);
}

/// <summary>Internal event the WhatsApp Node service POSTs back when Baileys reports a connection-state change
/// or an incoming message. For a message event, the message itself is already persisted by the Node service
/// directly into the shared "whatsappMessages" collection — these fields exist only so this event carries
/// enough to notify the user without a second round trip to re-fetch it.</summary>
public sealed record WhatsAppInternalEvent(
    string ConnectionId, string EventType, string? Status, string? Qr, DateTime? QrExpiresAt, string? AccountIdentifier, string? Error,
    string? MessageId = null, string? ChatId = null, string? ChatName = null, string? Preview = null, DateTime? Timestamp = null);

public static class IntegrationsModule
{
    private const string WhatsAppKey = "whatsapp";

    public static IServiceCollection AddDakshWhatsAppIntegration(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<WhatsAppIntegrationProvider>(c =>
        {
            var baseUrl = config["Integrations:Whatsapp:BaseUrl"] ?? "http://localhost:4100";
            c.BaseAddress = new Uri(baseUrl);
            c.DefaultRequestHeaders.Add("X-Internal-Key", config["Integrations:Whatsapp:InternalServiceKey"] ?? "");
        });
        services.AddSingleton<IIntegrationProvider>(sp => sp.GetRequiredService<WhatsAppIntegrationProvider>());
        return services;
    }

    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations").WithTags("Integrations");
        var authed = g.RequireAuthorization(AuthSetup.UserScheme);

        authed.MapGet("/", ListCatalogAsync);
        authed.MapGet("/{integrationKey}", GetIntegrationDetailAsync);
        authed.MapPost("/whatsapp/connect", ConnectWhatsAppAsync);
        authed.MapGet("/{connectionId}/qr", GetQrAsync);
        authed.MapGet("/{connectionId}/status", GetStatusAsync);
        authed.MapPost("/{connectionId}/reconnect", ReconnectAsync);
        authed.MapPost("/{connectionId}/disconnect", DisconnectAsync);
        authed.MapPatch("/{connectionId}/settings", UpdateConnectionSettingsAsync);
        authed.MapGet("/whatsapp/{connectionId}/chats", GetWhatsAppChatsAsync);
        authed.MapGet("/whatsapp/{connectionId}/chats/{chatId}/messages", GetWhatsAppChatMessagesAsync);
        authed.MapGet("/whatsapp/{connectionId}/media/{messageId}", GetWhatsAppMediaAsync);
        authed.MapPost("/whatsapp/{connectionId}/chats/{chatId}/messages", SendWhatsAppChatMessageAsync);
        authed.MapPost("/whatsapp/{connectionId}/send", SendNewWhatsAppMessageAsync);
        authed.MapPost("/whatsapp/{connectionId}/messages/{messageId}/react", ReactToWhatsAppMessageAsync);
        WhatsAppFeaturesModule.Map(authed);

        // System-to-system only: the WhatsApp Node service reports connection events here, authenticated by a
        // shared secret header (never the user JWT scheme — this isn't called on behalf of any one browser).
        g.MapPost("/whatsapp/internal/events", ReceiveWhatsAppEventAsync).AllowAnonymous();
    }

    /// <summary>Idempotent: creates the catalogue's unique index and seeds every known channel (WhatsApp enabled, the rest "coming soon").</summary>
    public static async Task EnsureIntegrationIndexesAndCatalogAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        var catalog = db.GetCollection<IntegrationCatalogEntry>("integrations");
        await catalog.Indexes.CreateOneAsync(
            new CreateIndexModel<IntegrationCatalogEntry>(
                Builders<IntegrationCatalogEntry>.IndexKeys.Ascending(c => c.IntegrationKey),
                new CreateIndexOptions { Unique = true, Name = "ux_integrations_integrationKey" }),
            cancellationToken: ct);

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        await connections.Indexes.CreateOneAsync(
            new CreateIndexModel<IntegrationConnection>(
                Builders<IntegrationConnection>.IndexKeys.Ascending(c => c.UserId).Ascending(c => c.IntegrationKey),
                new CreateIndexOptions { Name = "ix_integrationConnections_user_key" }),
            cancellationToken: ct);

        var credentials = db.GetCollection<IntegrationCredential>("integrationCredentials");
        await credentials.Indexes.CreateOneAsync(
            new CreateIndexModel<IntegrationCredential>(
                Builders<IntegrationCredential>.IndexKeys.Ascending(c => c.ConnectionId).Ascending(c => c.CredentialKey),
                new CreateIndexOptions { Unique = true, Name = "ux_integrationCredentials_connection_key" }),
            cancellationToken: ct);

        var dashboardApps = db.GetCollection<UserDashboardApp>("userDashboardApps");
        await dashboardApps.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDashboardApp>(
                Builders<UserDashboardApp>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.ConnectionId),
                new CreateIndexOptions { Unique = true, Name = "ux_userDashboardApps_user_connection" }),
            cancellationToken: ct);

        var messages = db.GetCollection<WhatsAppMessage>("whatsappMessages");
        await messages.Indexes.CreateOneAsync(
            new CreateIndexModel<WhatsAppMessage>(
                Builders<WhatsAppMessage>.IndexKeys.Ascending(m => m.ConnectionId).Descending(m => m.Timestamp),
                new CreateIndexOptions { Name = "ix_whatsappMessages_connection_timestamp" }),
            cancellationToken: ct);

        await SeedCatalogAsync(catalog, ct);
    }

    private static async Task SeedCatalogAsync(IMongoCollection<IntegrationCatalogEntry> catalog, CancellationToken ct)
    {
        // Mirrors apps/web/src/app/(app)/apps/page.tsx's channel list/colors so catalogue and UI never drift.
        (string Key, string Name, string Icon, string Color, string Category, bool Enabled)[] seed =
        [
            ("whatsapp", "WhatsApp", "SiWhatsapp", "#25D366", "Messaging", true),
            ("telegram", "Telegram", "SiTelegram", "#26A5E4", "Messaging", false),
            ("slack", "Slack", "mono:#", "#4A154B", "Messaging", false),
            ("microsoft-teams", "Microsoft Teams", "mono:T", "#6264A7", "Messaging", false),
            ("discord", "Discord", "SiDiscord", "#5865F2", "Messaging", false),
            ("google-chat", "Google Chat", "SiGooglechat", "#34A853", "Messaging", false),
            ("signal", "Signal", "SiSignal", "#3B45FD", "Messaging", false),
            ("line", "LINE", "SiLine", "#00C300", "Messaging", false),
            ("facebook-messenger", "Facebook Messenger", "SiMessenger", "#0866FF", "Messaging", false),
            ("instagram-messaging", "Instagram Messaging", "SiInstagram", "#FF0069", "Messaging", false),
            ("imessage", "iMessage", "SiImessage", "#34DA50", "Messaging", false),
            ("matrix", "Matrix", "SiMatrix", "#000000", "Messaging", false),
            ("mattermost", "Mattermost", "SiMattermost", "#0058CC", "Messaging", false),
            ("feishu-lark", "Feishu / Lark", "mono:L", "#3370FF", "Messaging", false),
            ("twitch-chat", "Twitch Chat", "SiTwitch", "#9146FF", "Messaging", false),
            ("sms-twilio", "SMS (Twilio)", "mono:SMS", "#F22F46", "Messaging", false),
            ("gmail", "Gmail", "SiGmail", "#EA4335", "Email", true),
            ("microsoft-outlook", "Microsoft Outlook Mail", "mono:O", "#0078D4", "Email", false),
            ("yahoo-mail", "Yahoo Mail", "mono:Y!", "#6001D2", "Email", false),
            ("zoho-mail", "Zoho Mail", "SiZoho", "#E42527", "Email", false),
            ("proton-mail", "Proton Mail", "SiProtonmail", "#6D4AFF", "Email", false),

            ("google-calendar", "Google Calendar", "SiGooglecalendar", "#4285F4", "Calendar", false),
            ("microsoft-outlook-calendar", "Microsoft Outlook Calendar", "mono:O", "#0078D4", "Calendar", false),
            ("apple-calendar", "Apple Calendar", "SiApple", "#000000", "Calendar", false),
            ("calendly", "Calendly", "SiCalendly", "#006BFF", "Calendar", false),

            ("zoom", "Zoom", "SiZoom", "#0B5CFF", "Meetings", false),

            ("google-drive", "Google Drive", "SiGoogledrive", "#4285F4", "Storage", false),
            ("onedrive", "OneDrive", "mono:OD", "#0078D4", "Storage", false),
            ("sharepoint", "SharePoint", "mono:SP", "#038387", "Storage", false),
            ("dropbox", "Dropbox", "SiDropbox", "#0061FF", "Storage", false),
            ("box", "Box", "SiBox", "#0061D5", "Storage", false),

            ("google-docs", "Google Docs", "SiGoogledocs", "#4285F4", "Documents", false),
            ("google-sheets", "Google Sheets", "SiGooglesheets", "#34A853", "Documents", false),
            ("google-slides", "Google Slides", "SiGoogleslides", "#FBBC04", "Documents", false),

            ("notion", "Notion", "SiNotion", "#000000", "Knowledge", false),
            ("confluence", "Confluence", "SiConfluence", "#172B4D", "Knowledge", false),

            ("airtable", "Airtable", "SiAirtable", "#18BFFF", "Data", false),

            ("facebook-pages", "Facebook Pages", "SiFacebook", "#0866FF", "Social", false),
            ("instagram-social", "Instagram", "SiInstagram", "#FF0069", "Social", false),
            ("x", "X", "SiX", "#000000", "Social", false),
            ("linkedin", "LinkedIn", "mono:in", "#0A66C2", "Social", false),
            ("youtube", "YouTube", "SiYoutube", "#FF0000", "Social", false),
            ("tiktok", "TikTok", "SiTiktok", "#000000", "Social", false),
            ("reddit", "Reddit", "SiReddit", "#FF4500", "Social", false),
            ("pinterest", "Pinterest", "SiPinterest", "#BD081C", "Social", false),
            ("mastodon", "Mastodon", "SiMastodon", "#6364FF", "Social", false),
            ("bluesky", "Bluesky", "SiBluesky", "#1185FE", "Social", false),

            ("github", "GitHub", "SiGithub", "#181717", "Developer", false),
            ("gitlab", "GitLab", "SiGitlab", "#FC6D26", "Developer", false),
            ("bitbucket", "Bitbucket", "SiBitbucket", "#0052CC", "Developer", false),
            ("azure-devops", "Azure DevOps", "mono:AD", "#0078D4", "Developer", false),
            ("jira", "Jira", "SiJira", "#0052CC", "Developer", false),
            ("sentry", "Sentry", "SiSentry", "#362D59", "Developer", false),
            ("docker-hub", "Docker Hub", "SiDocker", "#2496ED", "Developer", false),
            ("kubernetes", "Kubernetes", "SiKubernetes", "#326CE5", "Developer", false),

            ("aws", "AWS", "mono:AWS", "#FF9900", "Cloud", false),
            ("microsoft-azure", "Microsoft Azure", "mono:AZ", "#0078D4", "Cloud", false),
            ("google-cloud", "Google Cloud", "SiGooglecloud", "#4285F4", "Cloud", false),
            ("cloudflare", "Cloudflare", "SiCloudflare", "#F38020", "Cloud", false),
            ("vercel", "Vercel", "SiVercel", "#000000", "Cloud", false),
            ("supabase", "Supabase", "SiSupabase", "#3FCF8E", "Cloud", false),
            ("firebase", "Firebase", "SiFirebase", "#DD2C00", "Cloud", false),

            ("shopify", "Shopify", "SiShopify", "#7AB55C", "Commerce", false),
            ("woocommerce", "WooCommerce", "SiWoocommerce", "#96588A", "Commerce", false),
            ("ebay", "eBay", "SiEbay", "#E53238", "Commerce", false),
            ("amazon-selling-partner", "Amazon Selling Partner API", "mono:ASP", "#FF9900", "Commerce", false),

            ("razorpay", "Razorpay", "SiRazorpay", "#0C2451", "Payments", false),
            ("stripe", "Stripe", "SiStripe", "#635BFF", "Payments", false),
            ("paypal", "PayPal", "SiPaypal", "#002991", "Payments", false),

            ("quickbooks-online", "QuickBooks Online", "SiQuickbooks", "#2CA01C", "Accounting", false),
            ("xero", "Xero", "SiXero", "#13B5EA", "Accounting", false),
            ("zoho-books", "Zoho Books", "SiZoho", "#E42527", "Accounting", false),

            ("google-maps-platform", "Google Maps Platform", "SiGooglemaps", "#4285F4", "Maps", false),
            ("mapbox", "Mapbox", "SiMapbox", "#000000", "Maps", false),

            ("amadeus", "Amadeus", "mono:AM", "#003087", "Travel", false),
            ("booking-com", "Booking.com", "SiBookingdotcom", "#003A9A", "Travel", false),

            ("uber", "Uber", "SiUber", "#000000", "Mobility", false),

            ("irctc", "IRCTC", "mono:IR", "#1A5276", "India travel", false),

            ("digilocker", "DigiLocker", "mono:DL", "#0B5FFF", "India services", false),

            ("bhashini", "Bhashini", "mono:BH", "#FF6B35", "India AI", false),

            ("ondc", "ONDC", "mono:ON", "#00A651", "India commerce", false),

            ("home-assistant", "Home Assistant", "SiHomeassistant", "#18BCF2", "Smart home", false),
            ("philips-hue", "Philips Hue", "SiPhilipshue", "#0065D3", "Smart home", false),
            ("smartthings", "SmartThings", "SiSmartthings", "#15BFFF", "Smart home", false),
            ("tuya", "Tuya", "mono:TU", "#17D4D4", "Smart home", false),

            ("ifttt", "IFTTT", "SiIfttt", "#000000", "Automation", false),
            ("zapier", "Zapier", "SiZapier", "#FF4F00", "Automation", false),
            ("n8n", "n8n", "mono:n8n", "#EA4B71", "Automation", false),
            ("make", "Make", "SiMake", "#6D00CC", "Automation", false),

            ("mcp-servers", "MCP servers", "mono:MCP", "#64748B", "Integration protocol", false),

            ("openclaw-plugins", "OpenClaw Plugins", "mono:OC", "#64748B", "Agent runtime", false),
        ];

        foreach (var s in seed)
        {
            var description = s.Key == "whatsapp" ? "Connect your WhatsApp account to Daksha." : $"Connect your {s.Name} account to Daksha.";
            var update = Builders<IntegrationCatalogEntry>.Update
                .Set(c => c.Name, s.Name)
                .Set(c => c.Description, description)
                .Set(c => c.Icon, s.Icon)
                .Set(c => c.Color, s.Color)
                .Set(c => c.Category, s.Category)
                .SetOnInsert(c => c.CreatedAt, DateTime.UtcNow)
                .Set(c => c.UpdatedAt, DateTime.UtcNow)
                // IsEnabled is seeded only on first insert so flipping it on for a real launch later isn't clobbered by a restart.
                .SetOnInsert(c => c.IsEnabled, s.Enabled);
            await catalog.UpdateOneAsync(c => c.IntegrationKey == s.Key, update, new UpdateOptions { IsUpsert = true }, ct);
        }

        await catalog.UpdateOneAsync(c => c.IntegrationKey == "gmail",
            Builders<IntegrationCatalogEntry>.Update.Set(c => c.IsEnabled, true), cancellationToken: ct);
    }

    private static IIntegrationProvider? FindProvider(IEnumerable<IIntegrationProvider> providers, string key) =>
        providers.FirstOrDefault(p => p.IntegrationKey == key);

    private static async Task<IResult> ListCatalogAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(FilterDefinition<IntegrationCatalogEntry>.Empty)
            .SortBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct);
        return Results.Ok(catalog.Select(ToCatalogDto));
    }

    private static async Task<IResult> GetIntegrationDetailAsync(string integrationKey, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var entry = await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(c => c.IntegrationKey == integrationKey).FirstOrDefaultAsync(ct);
        if (entry is null) return Results.NotFound();

        var connection = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == who.UserId && c.IntegrationKey == integrationKey)
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);

        return Results.Ok(new IntegrationDetailDto(ToCatalogDto(entry), connection is null ? null : ToStatusDto(connection)));
    }

    private static async Task<IResult> ConnectWhatsAppAsync(IMongoDatabase db, IEnumerable<IIntegrationProvider> providers, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(c => c.IntegrationKey == WhatsAppKey).FirstOrDefaultAsync(ct);
        if (catalog is null || !catalog.IsEnabled) return Results.Problem(statusCode: 404, title: "WhatsApp is not available yet.");

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var existing = await connections.Find(c => c.UserId == who.UserId && c.IntegrationKey == WhatsAppKey && c.Status != ConnectionStatus.Disconnected)
            .FirstOrDefaultAsync(ct);

        IntegrationConnection connection;
        if (existing is not null)
        {
            // Already live (Connected or actively Connecting) — do NOT call the provider again. Baileys
            // starting a second session on top of a live one isn't a no-op: WhatsApp sees it as a
            // conflicting device and force-logs the real one out ("device_removed"). Just hand back what's
            // already there; the caller can reconnect/disconnect explicitly if they actually want a reset.
            if (existing.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
                return Results.Ok(new ConnectResponse(existing.Id, existing.Status.ToString()));

            connection = existing;
            await connections.UpdateOneAsync(c => c.Id == connection.Id,
                Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connecting).Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
        }
        else
        {
            connection = new IntegrationConnection
            {
                UserId = who.UserId,
                IntegrationKey = WhatsAppKey,
                Status = ConnectionStatus.Connecting,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await connections.InsertOneAsync(connection, cancellationToken: ct);
        }

        var provider = FindProvider(providers, WhatsAppKey)!;
        try
        {
            await provider.ConnectAsync(connection.Id, ct);
        }
        catch (Exception ex)
        {
            await connections.UpdateOneAsync(c => c.Id == connection.Id,
                Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Error).Set(c => c.LastError, ex.Message).Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
            return Results.Problem(statusCode: 502, title: "Could not start the WhatsApp connection service.", detail: ex.Message);
        }

        return Results.Ok(new ConnectResponse(connection.Id, ConnectionStatus.Connecting.ToString()));
    }

    private static async Task<IResult> GetQrAsync(string connectionId, IMongoDatabase db, IEnumerable<IIntegrationProvider> providers, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var provider = FindProvider(providers, connection.IntegrationKey);
        if (provider is null) return Results.Problem(statusCode: 404, title: "No provider for this connection.");

        var qr = await provider.GetQrAsync(connectionId, ct);
        return Results.Ok(qr);
    }

    private static async Task<IResult> GetStatusAsync(string connectionId, IMongoDatabase db, IEnumerable<IIntegrationProvider> providers, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var provider = FindProvider(providers, connection.IntegrationKey);
        if (provider is not null)
        {
            // Reconcile with the provider's live state rather than trusting only what's cached in Mongo —
            // the Node service may have reported an event this API missed (e.g. it restarted mid-flight).
            var liveStatus = await provider.GetStatusAsync(connectionId, ct);
            if (liveStatus != connection.Status)
            {
                await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(
                    c => c.Id == connectionId,
                    Builders<IntegrationConnection>.Update.Set(c => c.Status, liveStatus).Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
                connection.Status = liveStatus;
            }
        }

        return Results.Ok(ToStatusDto(connection));
    }

    private static async Task<IResult> ReconnectAsync(string connectionId, IMongoDatabase db, IEnumerable<IIntegrationProvider> providers, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var provider = FindProvider(providers, connection.IntegrationKey);
        if (provider is null) return Results.Problem(statusCode: 404, title: "No provider for this connection.");

        await provider.ReconnectAsync(connectionId, ct);
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(
            c => c.Id == connectionId,
            Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connecting).Set(c => c.LastError, null).Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
        return Results.Ok(new { status = ConnectionStatus.Connecting.ToString() });
    }

    private static async Task<IResult> DisconnectAsync(string connectionId, IMongoDatabase db, IEnumerable<IIntegrationProvider> providers, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var provider = FindProvider(providers, connection.IntegrationKey);
        if (provider is not null) await provider.DisconnectAsync(connectionId, ct);

        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(
            c => c.Id == connectionId,
            Builders<IntegrationConnection>.Update
                .Set(c => c.Status, ConnectionStatus.Disconnected)
                .Set(c => c.DisconnectedAt, DateTime.UtcNow)
                .Set(c => c.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);
        return Results.Ok(new { status = ConnectionStatus.Disconnected.ToString() });
    }

    private static async Task<IResult> ReceiveWhatsAppEventAsync(
        WhatsAppInternalEvent evt, IMongoDatabase db, IConfiguration config, IHubContext<IntegrationsHub> hub, HttpContext http,
        IAssistantOrchestrator orchestrator, WhatsAppIntegrationProvider whatsApp, IEmailSender emailSender, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var expected = config["Integrations:Whatsapp:InternalServiceKey"];
        if (string.IsNullOrEmpty(expected) || http.Request.Headers["X-Internal-Key"] != expected)
            return Results.Unauthorized();

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await connections.Find(c => c.Id == evt.ConnectionId).FirstOrDefaultAsync(ct);
        if (connection is null) return Results.NotFound();

        var update = Builders<IntegrationConnection>.Update.Set(c => c.UpdatedAt, DateTime.UtcNow);
        if (evt.Status is not null && Enum.TryParse<ConnectionStatus>(evt.Status, out var status))
        {
            update = update.Set(c => c.Status, status);
            if (status == ConnectionStatus.Connected)
                update = update.Set(c => c.ConnectedAt, DateTime.UtcNow).Set(c => c.LastActiveAt, DateTime.UtcNow);
        }
        if (evt.AccountIdentifier is not null) update = update.Set(c => c.AccountIdentifier, evt.AccountIdentifier);
        if (evt.Error is not null) update = update.Set(c => c.LastError, evt.Error);
        // A message event carries no Status, but it's still activity worth reflecting on the connection —
        // true for both directions, since sending counts as activity too.
        var isMessageEvent = evt.EventType is IntegrationEvents.MessageReceived or IntegrationEvents.MessageSent;
        if (isMessageEvent) update = update.Set(c => c.LastActiveAt, DateTime.UtcNow);
        await connections.UpdateOneAsync(c => c.Id == evt.ConnectionId, update, cancellationToken: ct);

        var isGroup = evt.ChatId is not null && WhatsAppChat.IsGroup(evt.ChatId);
        var settings = await WhatsAppSettingsModule.GetOrCreateAsync(db, evt.ConnectionId, ct);
        var logger = loggerFactory.CreateLogger("WhatsAppAutoReply");
        var isReceived = evt.EventType == IntegrationEvents.MessageReceived && evt.ChatId is not null && evt.Preview is not null;

        var chatRule = evt.ChatId is null ? null : await WhatsAppAutomationModule.GetChatRuleAsync(db, evt.ConnectionId, evt.ChatId, ct);
        var contact = isReceived && !isGroup ? await LookupContactAsync(db, evt.ConnectionId, evt.ChatId!, ct) : null;
        var isSavedContact = !string.IsNullOrWhiteSpace(contact?.Name);
        var isBusiness = contact?.IsBusiness ?? false;
        var highPriority = chatRule?.Priority == "High";

        // A chat switched to "ignore" is skipped entirely: no notification, no auto-reply, no rules.
        var monitored = chatRule?.Monitor ?? true;
        var passesMonitoring = monitored && (!isReceived || WhatsAppMonitoring.Passes(settings, isGroup, evt.Preview!, isSavedContact, isBusiness));

        // What the message is about (Message Intelligence) and which user-defined rules it triggers.
        var tags = new List<string>();
        var matchedRules = new List<WhatsAppAutomationRule>();
        if (isReceived && monitored)
        {
            tags = WhatsAppIntel.Detect(evt.Preview!, settings.DisabledIntelligenceTags);
            if (evt.MessageId is not null)
                await db.GetCollection<WhatsAppMessage>("whatsappMessages").UpdateOneAsync(
                    m => m.ConnectionId == evt.ConnectionId && m.MessageId == evt.MessageId, Builders<WhatsAppMessage>.Update.Set(m => m.Tags, tags), cancellationToken: ct);
            foreach (var tag in tags.Where(WhatsAppIntel.Extractable.Contains))
                await WhatsAppAutomationModule.AddInsightAsync(db, new WhatsAppInsight
                {
                    ConnectionId = evt.ConnectionId, ChatId = evt.ChatId!, ChatName = evt.ChatName, Kind = tag,
                    Text = evt.Preview!.Length <= 300 ? evt.Preview : evt.Preview[..300] + "…",
                    Value = WhatsAppIntel.ExtractValue(tag, evt.Preview), CreatedAt = DateTime.UtcNow,
                }, ct);
            matchedRules = await WhatsAppAutomationModule.MatchRulesAsync(
                db, evt.ConnectionId, evt.ChatId!, isGroup, evt.Preview!, tags, highPriority, !IsWithinActiveHours(settings), ct);
        }

        // Notification decision: global switch + Monitoring categories + (if filtered) the chosen notification
        // triggers. High-priority chats always notify, and a rule with a Notify action forces one.
        var passesNotifyFilter = settings.NotifyScope != "Filtered" || highPriority
            || tags.Any(settings.NotifyTags.Contains)
            || (settings.NotifyTags.Contains("unknown-number") && !isGroup && !isSavedContact)
            || (settings.NotifyTags.Contains("priority-contact") && highPriority);
        var forceNotify = matchedRules.Any(r => r.Actions.Any(a => a.Type == "Notify"));
        var notify = forceNotify || (connection.NotificationsEnabled && passesMonitoring && passesNotifyFilter);

        // The event always goes out so an open chat view stays in sync; `notify` says whether it's also a
        // bell/toast notification. MessageSent has its own flag (Outgoing message notifications).
        await IntegrationEvents.PublishAsync(hub, connection.UserId, evt.EventType, new
        {
            connectionId = evt.ConnectionId,
            status = evt.Status,
            qr = evt.Qr,
            qrExpiresAt = evt.QrExpiresAt,
            accountIdentifier = evt.AccountIdentifier,
            error = evt.Error,
            messageId = evt.MessageId,
            chatId = evt.ChatId,
            // For our own outgoing message evt.ChatName is *our* pushName, so resolve the recipient's name instead.
            chatName = evt.EventType == IntegrationEvents.MessageSent && evt.ChatId is not null
                ? await ResolveRecipientNameAsync(db, evt.ConnectionId, evt.ChatId, ct)
                : evt.ChatName,
            notify,
            notifyOutgoing = evt.EventType == IntegrationEvents.MessageSent && settings.OutgoingNotificationsEnabled,
            tags,
            priority = highPriority,
            sound = settings.NotificationSound,
            desktop = settings.DeliverDesktop,
            isGroup,
            preview = settings.HideMessagePreviews ? null : evt.Preview,
            timestamp = evt.Timestamp,
        }, ct);

        if (!isReceived || !monitored) return Results.Ok();

        // Everything below can take a while (AI calls, sends), so it runs after the notification above.
        if (notify) await DeliverExternalNotificationsAsync(db, emailSender, whatsApp, connection, settings, evt.ChatName, evt.Preview, logger, ct);

        var repliedByRule = false;
        if (matchedRules.Count > 0)
        {
            var userContext = new UserAiContext(connection.UserId, MemoryEnabled: false, AllowProviderDataSharing: true);
            await WhatsAppAutomationModule.RunStoreActionsAsync(db, orchestrator, userContext, matchedRules, evt.ConnectionId, evt.ChatId!, evt.ChatName, evt.Preview!, logger, ct);
            foreach (var action in matchedRules.SelectMany(r => r.Actions))
            {
                if (action.Type == "AiReply")
                {
                    await TryAutoReplyAsync(connection, settings, chatRule, isBusiness, force: true, evt.ChatId!, evt.ChatName, evt.Preview!, isGroup, db, orchestrator, whatsApp, logger, CancellationToken.None);
                    repliedByRule = true;
                }
                else if (action.Type == "FixedReply" && !settings.Paused && !string.IsNullOrWhiteSpace(action.Text))
                {
                    try { await whatsApp.SendMessageAsync(connection.Id, evt.ChatId!, action.Text, quotedMessageId: null, ct, isAiGenerated: false); repliedByRule = true; }
                    catch (Exception ex) { logger.LogWarning(ex, "Automation rule fixed reply failed for connection {ConnectionId}", connection.Id); }
                }
            }
        }

        // The connection-wide auto-reply, unless a rule already answered this message. A chat set to
        // "Always"/"Never" overrides the global switches.
        var autoReplyAllowed = chatRule?.AutoReply switch
        {
            "Always" => true,
            "Never" => false,
            _ => connection.AutoReplyEnabled && (!isGroup || connection.AutoReplyInGroups),
        };
        if (!repliedByRule && autoReplyAllowed && passesMonitoring && !IsCallNotice(evt.Preview))
            // The reply can legitimately take minutes (response delay, batching, the AI call). If the caller hangs up meanwhile -- the
        // WhatsApp service's HTTP client gives up after ~5 minutes, or it restarts -- the request token is cancelled; the reply must
        // still go out, so it deliberately does not observe that token (Telegram's auto-reply already works this way).
        await TryAutoReplyAsync(connection, settings, chatRule, isBusiness, force: false, evt.ChatId!, evt.ChatName, evt.Preview!, isGroup, db, orchestrator, whatsApp, logger, CancellationToken.None);

        return Results.Ok();
    }

    /// <summary>Generates a reply with Daksha's assistant and sends it back on WhatsApp, honouring every
    /// Auto Reply / AI Behaviour setting. Best-effort: any failure (no real AI model configured, Gemini
    /// error, WhatsApp send failure) is logged and swallowed — this runs off the back of a webhook ack, not
    /// a request a human is waiting on.</summary>
    private static async Task TryAutoReplyAsync(
        IntegrationConnection connection, WhatsAppSettings settings, WhatsAppChatRule? chatRule, bool isBusiness, bool force,
        string chatId, string? chatName, string messageText, bool isGroup,
        IMongoDatabase db, IAssistantOrchestrator orchestrator, WhatsAppIntegrationProvider whatsApp, ILogger logger, CancellationToken ct)
    {
        try
        {
            // `force` is an automation rule asking for a reply: it bypasses scope, hours and cooldown (the rule
            // is the user's explicit instruction) but never the pause switch or a chat set to "Never".
            var receivedAt = DateTime.UtcNow.AddSeconds(-2); // the webhook fires within moments of the message
            if (settings.Paused) return;
            if (settings.AutoReplyBlockedChats.Contains(chatId) || chatRule?.AutoReply == "Never") return;
            var forcedIn = force || chatRule?.AutoReply == "Always";
            if (!forcedIn && settings.AutoReplyScope == "SelectedOnly" && !settings.AutoReplyAllowedChats.Contains(chatId)) return;

            var withinHours = IsWithinActiveHours(settings);
            var useOutsideHoursMessage = !force && !withinHours && !string.IsNullOrWhiteSpace(settings.OutsideHoursMessage);
            if (!force && !settings.OperateContinuously && !withinHours && !useOutsideHoursMessage) return;

            var messages = db.GetCollection<WhatsAppMessage>("whatsappMessages");

            if (!forcedIn && settings.AutoReplyScope == "UnknownOnly")
            {
                var everReplied = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe).AnyAsync(ct);
                if (everReplied) return; // "unknown contacts only" — we've talked to this one before
            }

            var autoReplyState = db.GetCollection<WhatsAppAutoReplyState>("whatsappAutoReplyState");
            if (!force && settings.CooldownSeconds > 0)
            {
                var state = await autoReplyState.Find(s => s.ConnectionId == connection.Id && s.ChatId == chatId).FirstOrDefaultAsync(ct);
                if (state is not null && (DateTime.UtcNow - state.LastRepliedAt).TotalSeconds < settings.CooldownSeconds) return;
            }

            // If the owner has been typing in this chat themselves a moment ago, they are in the conversation: the assistant must not
            // butt in with a reply that ignores what the two of them were just saying. (An automation rule is still allowed to answer.)
            if (!forcedIn)
            {
                var lastByOwner = await messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated)
                    .SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
                if (IsOwnerActive(lastByOwner?.Timestamp, DateTime.UtcNow))
                {
                    logger.LogInformation("Auto-reply skipped for {ChatId}: the owner was chatting here moments ago.", chatId);
                    return;
                }
            }

            // Several messages in a row → one reply. Each message takes a ticket for its chat and waits out the
            // batch window; only the newest ticket carries on, and it answers everything unanswered so far.
            var batchKey = $"{connection.Id}:{chatId}";
            var ticket = BatchTickets.AddOrUpdate(batchKey, 1, (_, t) => t + 1);
            if (settings.BatchWindowSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.BatchWindowSeconds), ct);
                if (BatchTickets.TryGetValue(batchKey, out var latest) && latest != ticket) return; // a newer message will answer
            }
            var batchText = await UnansweredMessagesTextAsync(messages, connection.Id, chatId, messageText, ct);
            messageText = batchText;

            string replyText;
            if (!force && !string.IsNullOrWhiteSpace(chatRule?.CustomReply))
            {
                replyText = chatRule.CustomReply;
            }
            else if (useOutsideHoursMessage)
            {
                replyText = settings.OutsideHoursMessage!;
            }
            else if (isBusiness && !force && !string.IsNullOrWhiteSpace(settings.BusinessMessage))
            {
                replyText = settings.BusinessMessage;
            }
            else if (settings.CustomMessageEnabled && !force)
            {
                if (string.IsNullOrWhiteSpace(settings.CustomMessage)) return;
                replyText = settings.CustomMessage;
            }
            else
            {
                if (settings.AiExcludedChats.Contains(chatId) || chatRule?.AiExcluded == true) return; // never sent to the AI

                // The rule gate: the AI only ever answers simple social messages. Media/reels/emoji get nothing; anything that needs a
                // decision or information is held for the boss (a task is created) and the sender gets a holding reply instead.
                var gate = await ReplyGateFlow.ResolveAsync(db, settings, connection.Id, chatId, chatName, messageText, isGroup,
                    since => messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp > since).AnyAsync(ct),
                    DateTime.UtcNow, forced: null, ct, inConversation: await IsConversationBusyAsync(messages, connection.Id, chatId, ct));
                if (gate.Action == GateAction.Silent)
                {
                    logger.LogInformation("Reply gate: no reply for {ChatId} ({Reason})", chatId, gate.Reason);
                    return;
                }
                if (gate.Action == GateAction.SendHolding) { replyText = gate.Text!; goto Deliver; }

                // Turning auto-reply on for this connection IS the user's explicit, per-channel opt-in to let
                // AI see and answer these messages — that opt-in is the provider-data-sharing consent here.
                var userContext = new UserAiContext(connection.UserId, MemoryEnabled: false, AllowProviderDataSharing: true);
                var summary = await SummarizeOlderConversationAsync(settings, connection.Id, chatId, messages, orchestrator, userContext, logger, ct);
                var prompt = BuildAutoReplyPrompt(settings, chatId, chatName, messageText, isGroup, summary);
                var history = await BuildAutoReplyHistoryAsync(settings, connection.Id, chatId, messages, ct);
                var result = await orchestrator.RespondAsync(new AssistantRequest(userContext, prompt, history));

                if (result.IsSimulated || string.IsNullOrWhiteSpace(result.Text))
                {
                    logger.LogInformation("Skipped WhatsApp auto-reply for connection {ConnectionId}: no real AI model is configured.", connection.Id);
                    return;
                }
                if (result.Text.Trim().Equals("NO_REPLY", StringComparison.OrdinalIgnoreCase)) return; // hit a never-respond topic
                if (ReplyGate.ModelSaidNeedBoss(result.Text))
                {
                    // Second line of defence: the model itself judged this beyond small talk. Same as a gate hold.
                    var hold = await ReplyGateFlow.ResolveAsync(db, settings, connection.Id, chatId, chatName, messageText, isGroup,
                        since => messages.Find(m => m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp > since).AnyAsync(ct),
                        DateTime.UtcNow, GateVerdict.Boss, ct);
                    if (hold.Action != GateAction.SendHolding) return;
                    replyText = hold.Text!;
                }
                else replyText = result.Text;
            }

            Deliver:
            if (settings.ResponseDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(settings.ResponseDelaySeconds), ct);

            // If you answered this chat yourself (from your phone or Daksha's chat view) while the reply was
            // being generated or delayed, don't talk over you. Daksha's own sends are flagged IsAiGenerated.
            var repliedByUser = await messages.Find(m =>
                m.ConnectionId == connection.Id && m.ChatId == chatId && m.FromMe && !m.IsAiGenerated && m.Timestamp >= receivedAt)
                .AnyAsync(ct);
            if (repliedByUser) return;

            if (settings.RequireApproval)
            {
                await db.GetCollection<WhatsAppPendingReply>("whatsappPendingReplies").InsertOneAsync(new WhatsAppPendingReply
                {
                    ConnectionId = connection.Id,
                    ChatId = chatId,
                    ChatName = chatName,
                    Text = replyText,
                    CreatedAt = DateTime.UtcNow,
                }, cancellationToken: ct);
                return;
            }

            // A brief WhatsApp disconnect shouldn't throw away a reply that was already written: retry a few times.
            for (var attempt = 1; ; attempt++)
            {
                try { await whatsApp.SendMessageAsync(connection.Id, chatId, replyText, quotedMessageId: null, CancellationToken.None, isAiGenerated: true); break; }
                catch (InvalidOperationException) when (attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(6 * attempt)); }
            }
            await autoReplyState.UpdateOneAsync(
                s => s.ConnectionId == connection.Id && s.ChatId == chatId,
                Builders<WhatsAppAutoReplyState>.Update.Set(s => s.LastRepliedAt, DateTime.UtcNow).SetOnInsert(s => s.ConnectionId, connection.Id).SetOnInsert(s => s.ChatId, chatId),
                new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WhatsApp auto-reply failed for connection {ConnectionId}", connection.Id);
        }
    }

    /// <summary>Latest batch ticket per (connection, chat); in-memory, so it assumes a single API instance.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> BatchTickets = new();

    /// <summary>The incoming messages since the last time anyone replied in this chat, oldest first, as one text.
    /// Falls back to the triggering message when none are found.</summary>
    private static async Task<string> UnansweredMessagesTextAsync(IMongoCollection<WhatsAppMessage> messages, string connectionId, string chatId, string fallback, CancellationToken ct)
    {
        var lastReply = await messages.Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.FromMe)
            .SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
        var since = lastReply?.Timestamp ?? DateTime.MinValue;
        var pending = await messages.Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && !m.FromMe && m.Timestamp > since)
            .SortByDescending(m => m.Timestamp).Limit(10).ToListAsync(ct);
        if (pending.Count <= 1) return fallback;
        pending.Reverse();
        return string.Join("\n", pending.Select(m => m.Text));
    }

    /// <summary>True when this chat has had at least three messages (either side) in the last 30 minutes.</summary>
    private static async Task<bool> IsConversationBusyAsync(IMongoCollection<WhatsAppMessage> messages, string connectionId, string chatId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMinutes(-30);
        return await messages.CountDocumentsAsync(m => m.ConnectionId == connectionId && m.ChatId == chatId && m.Timestamp > since, new CountOptions { Limit = 3 }, ct) >= 3;
    }

    /// <summary>How long after the owner's own (non-AI) message in a chat the assistant stays out of it.</summary>
    public static readonly TimeSpan OwnerActiveQuiet = TimeSpan.FromMinutes(15);

    /// <summary>True when the owner wrote in this chat themselves within <see cref="OwnerActiveQuiet"/> of now.</summary>
    public static bool IsOwnerActive(DateTime? lastOwnerMessageUtc, DateTime nowUtc) =>
        lastOwnerMessageUtc is { } last && nowUtc - last >= TimeSpan.Zero && nowUtc - last < OwnerActiveQuiet;

    internal static bool IsWithinActiveHours(WhatsAppSettings settings) => IsWithinActiveHours(settings, DateTime.UtcNow);

    internal static bool IsWithinActiveHours(WhatsAppSettings settings, DateTime utcNow)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(settings.Timezone); }
        catch { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var now = local.TimeOfDay;
        var day = (int)local.DayOfWeek;

        if (!TimeSpan.TryParse(settings.ActiveHoursStart, out var start) || !TimeSpan.TryParse(settings.ActiveHoursEnd, out var end))
            return true; // malformed config shouldn't silently block every reply

        // The selected day is the one the window *starts* on, so an overnight Mon 22:00–06:00 shift still
        // covers Tuesday's small hours.
        bool DayOn(int d) => settings.ActiveDays.Contains(((d % 7) + 7) % 7);
        if (start <= end) return DayOn(day) && now >= start && now <= end;
        return (now >= start && DayOn(day)) || (now <= end && DayOn(day - 1));
    }

    private static bool IsCallNotice(string? text) =>
        text is not null && (text.StartsWith("📞", StringComparison.Ordinal) || text.StartsWith("📹", StringComparison.Ordinal));

    /// <summary>The contact record (saved name, business flag) for a chat, found by its own id or its @lid alias.</summary>
    private static async Task<WhatsAppContact?> LookupContactAsync(IMongoDatabase db, string connectionId, string chatId, CancellationToken ct)
    {
        // A chat can have more than one record (one keyed by its phone id, one by its @lid alias, one written by
        // the business-profile check), each carrying different fields — merge them rather than trust the first.
        var matches = await db.GetCollection<WhatsAppContact>("whatsappContacts")
            .Find(c => c.ConnectionId == connectionId && (c.ContactId == chatId || c.Lid == chatId)).ToListAsync(ct);
        if (matches.Count == 0) return null;
        return new WhatsAppContact
        {
            ConnectionId = connectionId,
            ContactId = chatId,
            Name = matches.Select(c => c.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            Notify = matches.Select(c => c.Notify).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            IsBusiness = matches.Any(c => c.IsBusiness),
        };
    }

    /// <summary>Optional extra delivery channels for a notification: an email to the account's address and/or a
    /// WhatsApp message to the connected number itself ("Message yourself"). Each is best-effort and independent.</summary>
    private static async Task DeliverExternalNotificationsAsync(
        IMongoDatabase db, IEmailSender emailSender, WhatsAppIntegrationProvider whatsApp, IntegrationConnection connection,
        WhatsAppSettings settings, string? chatName, string? preview, ILogger logger, CancellationToken ct)
    {
        var who = chatName ?? "a contact";
        var body = settings.HideMessagePreviews || preview is null ? "(message preview hidden)" : preview;

        if (settings.DeliverEmail)
        {
            try
            {
                var user = await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == connection.UserId).FirstOrDefaultAsync(ct);
                if (user is not null) await emailSender.SendAsync(user.Email, $"New WhatsApp message from {who}", $"{who} wrote on WhatsApp:\n\n{body}", ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Email notification failed for connection {ConnectionId}", connection.Id); }
        }

        if (settings.DeliverWhatsAppSelf && !string.IsNullOrEmpty(connection.AccountIdentifier))
        {
            try
            {
                var self = connection.AccountIdentifier.Split(':')[0].Split('@')[0] + "@s.whatsapp.net";
                await whatsApp.SendMessageAsync(connection.Id, self, $"🔔 {who}: {body}", quotedMessageId: null, ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "WhatsApp self-notification failed for connection {ConnectionId}", connection.Id); }
        }
    }

    /// <summary>When enabled, condenses the part of the conversation older than the raw context window into a
    /// short summary (one extra AI call). Best-effort: any failure just means replying without a summary.</summary>
    private static async Task<string?> SummarizeOlderConversationAsync(
        WhatsAppSettings settings, string connectionId, string chatId, IMongoCollection<WhatsAppMessage> messages,
        IAssistantOrchestrator orchestrator, UserAiContext userContext, ILogger logger, CancellationToken ct)
    {
        if (!settings.SummarizeLongConversations || !settings.AllowMessageContentAsAiContext || !settings.UseConversationContext) return null;
        try
        {
            var older = await messages.Find(m => m.ConnectionId == connectionId && m.ChatId == chatId)
                .SortByDescending(m => m.Timestamp).Skip(Math.Max(0, settings.ContextRetentionMessages)).Limit(40).ToListAsync(ct);
            if (older.Count < 5) return null; // nothing long enough to be worth an extra AI call
            older.Reverse();
            var transcript = string.Join("\n", older.Select(m => $"{(m.FromMe ? "Me" : "Them")}: {m.Text}"));
            var result = await orchestrator.RespondAsync(new AssistantRequest(userContext,
                "Summarize this earlier WhatsApp conversation in two or three short sentences, keeping names, commitments and open questions. " +
                "Output only the summary.\n\n" + transcript), ct);
            return result.IsSimulated || string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not summarize the conversation for connection {ConnectionId}; replying without a summary", connectionId);
            return null;
        }
    }

    public static string BuildAutoReplyPrompt(WhatsAppSettings settings, string chatId, string? chatName, string messageText, bool isGroup, string? summary = null, string channel = "WhatsApp")
    {
        var who = chatName ?? chatId;
        // Explicit about point of view and output format: without this, a model asked to "reply on someone's
        // behalf" tends to hedge with meta-commentary ("here are two options depending on whether...")
        // instead of just answering in character the way a person actually texting would.
        var scope = isGroup
            ? $"inside a group chat named \"{who}\". Only reply if it genuinely helps the conversation"
            : $"to a direct message from \"{who}\"";

        var personality = settings.Personality == "Custom" && !string.IsNullOrWhiteSpace(settings.CustomPersonality)
            ? settings.CustomPersonality
            : settings.Personality switch
            {
                "Professional" => "Professional and polished",
                "Casual" => "Very casual, like texting a friend",
                "Formal" => "Formal and respectful",
                _ => "Friendly and warm",
            };
        var length = settings.ResponseLength switch
        {
            "Short" => "Keep it to one short sentence.",
            "Detailed" => "A couple of sentences is fine if it helps.",
            _ => "Keep it brief — a sentence or two.",
        };

        var boss = ReplyGate.NormaliseBoss(settings.BossReference);
        // The assistant is not the owner and must not decide anything for them. It only does small talk; for everything else it says NEED_BOSS
        // and the gate sends a holding reply. (The gate normally stops those messages before they reach this prompt; this is the backstop.)
        var instructions =
            $"You are the AI assistant that answers {channel} messages on behalf of the account owner, whom you refer to as \"{boss}\", " +
            $"replying {scope}. Write only the exact message text to send — never a list of options, never meta-commentary. " +
            "You may ONLY handle simple social messages: greetings, thanks, \"how are you\", good wishes, goodbyes. " +
            "You must NEVER make a decision, promise, commitment or confirmation, agree to anything, share any information or opinion, answer a " +
            "question of substance, discuss money, time, availability, documents or plans, or speak as if you were the owner. " +
            $"If the message needs any of that, output exactly NEED_BOSS and nothing else. Never reply to photos, reels, videos, stickers or voice notes. " +
            "The earlier messages in this conversation are shown to you: always read them first. A short or fragmentary message (for example just \"Office party\") " +
            "usually answers or asks about what was just discussed, so work out what it means from that context. If, in context, it asks a question, " +
            "is about plans, a favour, a ride, time or availability, or you are not sure what it means, output exactly NEED_BOSS. " +
            $"Tone: {personality}. {length}";

        if (!string.IsNullOrWhiteSpace(settings.CustomInstructions))
            instructions += $" Additional instructions from the account owner: {settings.CustomInstructions}";
        if (!string.IsNullOrWhiteSpace(settings.NeverRespondTopics))
            instructions += $" Never respond about any of these topics — if the message is about one of them, output exactly NO_REPLY and nothing else: {settings.NeverRespondTopics}";

        if (settings.ReplyInSenderLanguage)
            instructions += " Reply in the same language, and the same script (e.g. romanized vs native), that the message you're replying to is written in.";
        if (!string.IsNullOrWhiteSpace(summary))
            instructions += $" Background on the earlier conversation (older than the messages shown to you): {summary}";

        return messageText.Contains('\n')
            ? $"{instructions} They sent several messages in a row (oldest first, one per line). Read them all and answer them together in a single reply:\n{messageText}"
            : $"{instructions} The message you're replying to: \"{messageText}\"";
    }

    private static async Task<IReadOnlyList<(string Role, string Content)>?> BuildAutoReplyHistoryAsync(
        WhatsAppSettings settings, string connectionId, string chatId, IMongoCollection<WhatsAppMessage> messages, CancellationToken ct)
    {
        if (!settings.AllowMessageContentAsAiContext || !settings.UseConversationContext || settings.ContextRetentionMessages <= 0)
            return null;

        var recent = await messages.Find(m => m.ConnectionId == connectionId && m.ChatId == chatId)
            .SortByDescending(m => m.Timestamp).Limit(settings.ContextRetentionMessages).ToListAsync(ct);
        recent.Reverse();
        return recent.Select(m => (m.FromMe ? "assistant" : "user", m.Text)).ToList();
    }

    private static async Task<IResult> UpdateConnectionSettingsAsync(string connectionId, UpdateConnectionSettingsRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var update = Builders<IntegrationConnection>.Update.Set(c => c.UpdatedAt, DateTime.UtcNow);
        var changed = false;
        if (req.NotificationsEnabled is { } notify) { update = update.Set(c => c.NotificationsEnabled, notify); connection.NotificationsEnabled = notify; changed = true; }
        if (req.AutoReplyEnabled is { } autoReply) { update = update.Set(c => c.AutoReplyEnabled, autoReply); connection.AutoReplyEnabled = autoReply; changed = true; }
        if (req.AutoReplyInGroups is { } autoReplyGroups) { update = update.Set(c => c.AutoReplyInGroups, autoReplyGroups); connection.AutoReplyInGroups = autoReplyGroups; changed = true; }
        if (changed) await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == connectionId, update, cancellationToken: ct);
        return Results.Ok(ToStatusDto(connection));
    }

    /// <summary>One row per chat the connection has ever exchanged messages with, most recently active first —
    /// the Manage panel's chat list. Unread is relative to when the user last opened any chat on this connection
    /// (one read marker per connection, not per chat — same simplification the dashboard widget uses).</summary>
    /// <summary>
    /// The name to show for a chat: the name saved in the owner's phone, else the name the person chose for themselves, else (one-to-one chats
    /// only) the name on one of their own messages. A message we sent carries OUR name, and a group message carries the sender's, so those
    /// are never used for the chat.
    /// </summary>
    public static string? PickChatName(string chatId, IReadOnlyDictionary<string, string> saved, IReadOnlyDictionary<string, string> selfChosen, IEnumerable<(bool FromMe, string? ChatName)> messages) =>
        saved.GetValueOrDefault(chatId)
        ?? selfChosen.GetValueOrDefault(chatId)
        ?? (WhatsAppChat.IsGroup(chatId) ? null : messages.FirstOrDefault(m => !m.FromMe && !string.IsNullOrWhiteSpace(m.ChatName)).ChatName);

    private static async Task<IResult> GetWhatsAppChatsAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var lastViewedAt = connection.LastViewedAt ?? DateTime.MinValue;
        var all = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => m.ConnectionId == connectionId && !m.Hidden).SortByDescending(m => m.Timestamp).ToListAsync(ct);

        // The name you saved in your phone beats the name the person chose for themselves, which beats the name on one of their
        // messages. A person can be known by a phone id or a linked-device (@lid) id, so every id on a contact record points at the
        // same name.
        var contacts = await db.GetCollection<WhatsAppContact>("whatsappContacts").Find(c => c.ConnectionId == connectionId).ToListAsync(ct);
        var saved = new Dictionary<string, string>();
        var selfChosen = new Dictionary<string, string>();
        foreach (var c in contacts)
        {
            var ids = new[] { c.ContactId, c.Lid, c.PhoneNumber }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
            if (!string.IsNullOrWhiteSpace(c.Name)) foreach (var id in ids) saved[id] = c.Name!;
            if (!string.IsNullOrWhiteSpace(c.Notify)) foreach (var id in ids) selfChosen[id] = c.Notify!;
        }
        var prefs = (await db.GetCollection<WhatsAppChatPref>("whatsappChatPrefs").Find(p => p.ConnectionId == connectionId).ToListAsync(ct)).ToDictionary(p => p.ChatId);

        var chats = all
            .GroupBy(m => m.ChatId)
            .Select(g =>
            {
                var latest = g.First(); // the group's messages are already sorted newest-first
                var unread = g.Count(m => !m.FromMe && m.Timestamp > lastViewedAt);
                // An outgoing message's pushName is *our own* name, and a group message's is the sender's, so
                // only a 1:1 chat's incoming messages are a trustworthy last resort for the other person's name.
                var name = PickChatName(g.Key, saved, selfChosen, g.Select(m => (m.FromMe, m.ChatName)));
                prefs.TryGetValue(g.Key, out var pref);
                return new WhatsAppChatDto(g.Key, name, WhatsAppChat.IsGroup(g.Key), latest.Text, latest.Timestamp, unread,
                    pref?.Pinned ?? false, pref?.Archived ?? false, pref?.MutedUntil);
            })
            .OrderByDescending(c => c.Pinned).ThenByDescending(c => c.LastMessageAt);

        return Results.Ok(chats);
    }

    /// <summary>Full thread for one chat, oldest first. Marks the whole connection viewed, same as above.</summary>
    private static async Task<IResult> GetWhatsAppChatMessagesAsync(string connectionId, string chatId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var messages = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && !m.Hidden)
            .SortByDescending(m => m.Timestamp).Limit(200).ToListAsync(ct);

        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(
            c => c.Id == connectionId,
            Builders<IntegrationConnection>.Update.Set(c => c.LastViewedAt, DateTime.UtcNow), cancellationToken: ct);

        messages.Reverse();
        var isGroup = WhatsAppChat.IsGroup(chatId);
        return Results.Ok(messages.Select(m => ToMessageDto(m, isGroup)));
    }

    private static async Task<IResult> GetWhatsAppMediaAsync(string connectionId, string messageId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedConnectionAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var doc = await db.GetCollection<MongoDB.Bson.BsonDocument>("whatsappMedia")
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.And(
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("connectionId", connectionId),
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("messageId", messageId)))
            .FirstOrDefaultAsync(ct);
        if (doc is null || !doc.TryGetValue("data", out var data) || !data.IsBsonBinaryData) return Results.NotFound();
        var mime = doc.TryGetValue("mimeType", out var m) && m.IsString ? m.AsString : "image/jpeg";
        // Documents are offered as a download under their own file name; everything else plays or shows inline.
        var message = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(x => x.ConnectionId == connectionId && x.MessageId == messageId).FirstOrDefaultAsync(ct);
        return message?.MediaKind == "document"
            ? Results.File(data.AsBsonBinaryData.Bytes, mime, message.FileName ?? "file")
            : Results.File(data.AsBsonBinaryData.Bytes, mime, enableRangeProcessing: true);
    }

    private static async Task<IResult> SendWhatsAppChatMessageAsync(
        string connectionId, string chatId, SendWhatsAppMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "Message text is required.");

        try
        {
            await whatsApp.SendMessageAsync(connectionId, chatId, req.Text, req.QuotedMessageId, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(statusCode: 502, title: "Could not send the message.", detail: ex.Message);
        }
        // NoContent (204), not Ok() — a 200 with no body makes the client's res.json() throw on the empty
        // body, which apiFetch can't tell apart from a real failure; the frontend already special-cases 204.
        return Results.NoContent();
    }

    private static async Task<IResult> ReactToWhatsAppMessageAsync(
        string connectionId, string messageId, ReactToWhatsAppMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();

        var target = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => m.ConnectionId == connectionId && m.MessageId == messageId).FirstOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();

        try
        {
            await whatsApp.ReactAsync(connectionId, target.ChatId, messageId, req.Emoji, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(statusCode: 502, title: "Could not react to the message.", detail: ex.Message);
        }
        return Results.NoContent();
    }

    /// <summary>Starts (or continues) a conversation with a phone number the UI doesn't need an existing chat
    /// for — "To" is a phone number with country code; punctuation/spaces are stripped and it's turned into a
    /// WhatsApp individual JID.</summary>
    private static async Task<IResult> SendNewWhatsAppMessageAsync(
        string connectionId, SendNewWhatsAppMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, WhatsAppIntegrationProvider whatsApp, CancellationToken ct)
    {
        var connection = await OwnedConnectionAsync(db, connectionId, who.UserId, ct);
        if (connection is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "Message text is required.");

        var digits = new string(req.To.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return Results.Problem(statusCode: 400, title: "Enter a phone number with country code, e.g. 919876543210.");
        var chatId = $"{digits}@s.whatsapp.net";

        try
        {
            await whatsApp.SendMessageAsync(connectionId, chatId, req.Text, quotedMessageId: null, ct);
        }
        catch (Exception ex)
        {
            return Results.Problem(statusCode: 502, title: "Could not send the message.", detail: ex.Message);
        }
        return Results.Ok(new { chatId });
    }

    /// <summary>Saved contact/group name for a chat, else the other person's own display name from an incoming message.</summary>
    private static async Task<string?> ResolveRecipientNameAsync(IMongoDatabase db, string connectionId, string chatId, CancellationToken ct)
    {
        var contact = await LookupContactAsync(db, connectionId, chatId, ct);
        var saved = !string.IsNullOrWhiteSpace(contact?.Name) ? contact!.Name : contact?.Notify;
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        if (WhatsAppChat.IsGroup(chatId)) return null;

        var incoming = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => m.ConnectionId == connectionId && m.ChatId == chatId && !m.FromMe && m.ChatName != null)
            .SortByDescending(m => m.Timestamp).FirstOrDefaultAsync(ct);
        return incoming?.ChatName;
    }

    internal static Task<IntegrationConnection?> OwnedConnectionAsync(IMongoDatabase db, string connectionId, string userId, CancellationToken ct) =>
        db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.Id == connectionId && c.UserId == userId)
            .FirstOrDefaultAsync(ct)!;

    private static IntegrationCatalogDto ToCatalogDto(IntegrationCatalogEntry c) => new(c.IntegrationKey, c.Name, c.Description, c.Icon, c.Color, c.Category, c.IsEnabled);
    private static ConnectionStatusDto ToStatusDto(IntegrationConnection c) => new(c.Id, c.IntegrationKey, c.Status.ToString(), c.AccountIdentifier, c.ConnectedAt, c.LastActiveAt, c.LastError, c.NotificationsEnabled, c.AutoReplyEnabled, c.AutoReplyInGroups);
    private static WhatsAppMessageDto ToMessageDto(WhatsAppMessage m, bool isGroup) => new(
        m.MessageId, m.ChatId, m.ChatName, m.FromMe, m.IsAiGenerated, isGroup, m.Text,
        m.QuotedMessageId, m.QuotedText, m.Reactions.Select(r => new WhatsAppReactionDto(r.By, r.Emoji)).ToList(), m.Timestamp, m.HasMedia,
        m.MediaKind, m.MimeType, m.FileName, m.MediaSeconds, m.VoiceNote, m.Status, m.Deleted, m.Edited, m.Starred);
}

/// <summary>Tiny DI-bindable wrapper so endpoint handlers can take the caller's user id as a parameter like any other service.</summary>
public sealed class ClaimsPrincipalAccessor(IHttpContextAccessor accessor)
{
    public string UserId => accessor.HttpContext!.User.UserId();
}

/// <summary>Calls the WhatsApp Node service (Baileys) over HTTP, authenticated with the shared internal key.</summary>
public sealed class WhatsAppIntegrationProvider(HttpClient http, IMongoDatabase db) : IIntegrationProvider
{
    public string IntegrationKey => "whatsapp";

    public async Task ConnectAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.PostAsync($"/sessions/{connectionId}/connect", null, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<QrResponse> GetQrAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/qr", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return new QrResponse(null, null, ConnectionStatus.Connecting.ToString());
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<QrResponse>(cancellationToken: ct))!;
    }

    public async Task<ConnectionStatus> GetStatusAsync(string connectionId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/status", ct);
        if (!res.IsSuccessStatusCode) return ConnectionStatus.Error;
        var body = await res.Content.ReadFromJsonAsync<StatusBody>(cancellationToken: ct);
        return Enum.TryParse<ConnectionStatus>(body?.Status, out var s) ? s : ConnectionStatus.Error;
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

    /// <summary>Sends a text message to an existing chat or a brand-new contact JID alike, optionally quoting
    /// an earlier message. Not part of <see cref="IIntegrationProvider"/> — only WhatsApp has this shape
    /// today, so callers resolve this concrete type from DI directly rather than adding a send member every
    /// other provider would have to stub. <paramref name="isAiGenerated"/> is true only for Daksha's own
    /// auto-reply — never for anything the account owner typed — so the UI can tell the two apart.</summary>
    public async Task SendMessageAsync(string connectionId, string chatId, string text, string? quotedMessageId, CancellationToken ct, bool isAiGenerated = false)
    {
        // The "Reply by AI Assistant" line is a per-connection setting: on by default, with optional custom wording.
        string? signature = null;
        if (isAiGenerated)
        {
            var settings = await db.GetCollection<WhatsAppSettings>("whatsappSettings").Find(s => s.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
            if (settings?.AiSignatureEnabled ?? true)
                signature = string.IsNullOrWhiteSpace(settings?.AiSignatureText) ? WhatsAppSettings.DefaultAiSignature : settings!.AiSignatureText!.Trim();
        }
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/send", new { chatId, text, quotedMessageId, isAiGenerated, signature }, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Could not send the WhatsApp message: {body}");
        }
    }

    // ── Everything beyond text: media, read receipts, delete/edit/forward, typing, groups, pictures ─────────────────────────────

    /// <summary>One POST to the WhatsApp service; errors carry the service's own message.</summary>
    public async Task<System.Text.Json.JsonElement> CallAsync(string path, object body, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync(path, body, ct);
        await EnsureOkAsync(res, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        var message = body;
        try { using var doc = System.Text.Json.JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String) message = e.GetString() ?? body; } catch { /* not JSON: use the raw text */ }
        throw new InvalidOperationException(message);
    }

    public async Task SendMediaAsync(string connectionId, string chatId, string kind, byte[] data, string mimeType, string? fileName, string? caption, string? quotedMessageId, bool voiceNote, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/send-media",
            new { chatId, kind, data = Convert.ToBase64String(data), mimeType, fileName, caption, quotedMessageId, voiceNote }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task MarkReadAsync(string connectionId, string chatId, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/read", new { chatId }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task DeleteMessageAsync(string connectionId, string chatId, string messageId, bool forEveryone, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/delete", new { chatId, messageId, forEveryone }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task EditMessageAsync(string connectionId, string chatId, string messageId, string text, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/edit", new { chatId, messageId, text }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task ForwardMessageAsync(string connectionId, string toChatId, string messageId, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/forward", new { toChatId, messageId }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task SetPresenceAsync(string connectionId, string chatId, string state, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/presence", new { chatId, state }, ct);
        await EnsureOkAsync(res, ct);
    }

    public async Task<System.Text.Json.JsonElement> GetPresenceAsync(string connectionId, string chatId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/presence?chatId={Uri.EscapeDataString(chatId)}", ct);
        await EnsureOkAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
    }

    public async Task<System.Text.Json.JsonElement> GetGroupAsync(string connectionId, string chatId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/group?chatId={Uri.EscapeDataString(chatId)}", ct);
        await EnsureOkAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
    }

    /// <summary>The chat's profile (or group) picture, or null when it has none or hides it.</summary>
    public async Task<(byte[] Data, string MimeType)?> GetProfilePictureAsync(string connectionId, string chatId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/sessions/{connectionId}/profile-picture?chatId={Uri.EscapeDataString(chatId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return (await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    /// <summary>Reacts to an existing message with an emoji, or clears a prior reaction by passing "".</summary>
    public async Task ReactAsync(string connectionId, string chatId, string messageId, string emoji, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/sessions/{connectionId}/react", new { chatId, messageId, emoji }, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Could not react to the WhatsApp message: {body}");
        }
    }

    private sealed record StatusBody(string Status);
}
