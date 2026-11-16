using System.Net.Http.Json;
using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record IntegrationCatalogDto(string IntegrationKey, string Name, string Description, string Icon, string Color, string Category, bool IsEnabled);
public sealed record ConnectionStatusDto(string ConnectionId, string IntegrationKey, string Status, string? AccountIdentifier, DateTime? ConnectedAt, DateTime? LastActiveAt, string? LastError);
public sealed record IntegrationDetailDto(IntegrationCatalogDto Catalog, ConnectionStatusDto? Connection);
public sealed record QrResponse(string? Qr, DateTime? ExpiresAt, string Status);
public sealed record ConnectResponse(string ConnectionId, string Status);

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

/// <summary>Internal event the WhatsApp Node service POSTs back when Baileys reports a connection-state change.</summary>
public sealed record WhatsAppInternalEvent(string ConnectionId, string EventType, string? Status, string? Qr, DateTime? QrExpiresAt, string? AccountIdentifier, string? Error);

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

    private static async Task<IResult> ReceiveWhatsAppEventAsync(WhatsAppInternalEvent evt, IMongoDatabase db, IConfiguration config, IHubContext<IntegrationsHub> hub, HttpContext http, CancellationToken ct)
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
        await connections.UpdateOneAsync(c => c.Id == evt.ConnectionId, update, cancellationToken: ct);

        await IntegrationEvents.PublishAsync(hub, connection.UserId, evt.EventType, new
        {
            connectionId = evt.ConnectionId,
            status = evt.Status,
            qr = evt.Qr,
            qrExpiresAt = evt.QrExpiresAt,
            accountIdentifier = evt.AccountIdentifier,
            error = evt.Error,
        }, ct);

        return Results.Ok();
    }

    private static Task<IntegrationConnection?> OwnedConnectionAsync(IMongoDatabase db, string connectionId, string userId, CancellationToken ct) =>
        db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.Id == connectionId && c.UserId == userId)
            .FirstOrDefaultAsync(ct)!;

    private static IntegrationCatalogDto ToCatalogDto(IntegrationCatalogEntry c) => new(c.IntegrationKey, c.Name, c.Description, c.Icon, c.Color, c.Category, c.IsEnabled);
    private static ConnectionStatusDto ToStatusDto(IntegrationConnection c) => new(c.Id, c.IntegrationKey, c.Status.ToString(), c.AccountIdentifier, c.ConnectedAt, c.LastActiveAt, c.LastError);
}

/// <summary>Tiny DI-bindable wrapper so endpoint handlers can take the caller's user id as a parameter like any other service.</summary>
public sealed class ClaimsPrincipalAccessor(IHttpContextAccessor accessor)
{
    public string UserId => accessor.HttpContext!.User.UserId();
}

/// <summary>Calls the WhatsApp Node service (Baileys) over HTTP, authenticated with the shared internal key.</summary>
public sealed class WhatsAppIntegrationProvider(HttpClient http) : IIntegrationProvider
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

    private sealed record StatusBody(string Status);
}
