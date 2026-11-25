using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// Slack: one IntegrationConnection ("slack") per Slack workspace, signed in with Slack's OAuth (a *user* token, so Daksha acts as the person
// and sees only what they can see). One encrypted token per workspace. Read-only by default; full access adds the permissions to post and
// mark read. Inbound activity (DMs and mentions) arrives through the Events API webhook when the Slack app is configured for it.

public sealed record SlackWorkspaceDto(string ConnectionId, string TeamId, string TeamName, string SlackUserId, bool Write, int Unread);
public sealed record SlackStatusDto(bool Configured, bool EventsConfigured, bool Connected, IReadOnlyList<SlackWorkspaceDto> Workspaces, string? Error);
public sealed record SlackAuthorizationResponse(string AuthorizationUrl);
public sealed record SetSlackWriteRequest(string? ConnectionId, bool Enabled);
public sealed record SlackSettingsDto(int DashboardCount, string DashboardSource, bool MarkReadOnOpen, bool NotifyDms, bool NotifyMentions, bool HideBots, string? DefaultConnectionId, bool ShowOnDashboard);
public sealed record UpdateSlackSettingsRequest(int? DashboardCount, string? DashboardSource, bool? MarkReadOnOpen, bool? NotifyDms, bool? NotifyMentions, bool? HideBots, string? DefaultConnectionId);
public sealed record SlackChannelDto(string Id, string Name, string Kind, bool IsPrivate, string? Topic, int? Members, string? UserId);
public sealed record SlackMessageDto(string Ts, string UserId, string UserName, string Text, string? ThreadTs, int ReplyCount, DateTimeOffset? Time, bool IsBot, int Files);
public sealed record SlackMessagePage(IReadOnlyList<SlackMessageDto> Messages, string? NextCursor);
public sealed record SendSlackMessageRequest(string? Text, string? ThreadTs);
public sealed record SlackSearchHit(string ChannelId, string Channel, string UserName, string Text, string Ts, string? Permalink);
public sealed record SlackActivityDto(string Id, string ChannelId, string Channel, string Kind, string UserName, string Text, DateTimeOffset Time, bool Read);
public sealed record SlackDashboardDto(bool Connected, string? TeamName, int Unread, IReadOnlyList<SlackActivityDto> Activity, IReadOnlyList<SlackChannelDto> Conversations, bool EventsConfigured, IReadOnlyList<string> Errors);

[BsonIgnoreExtraElements]
public sealed class SlackAccountDoc
{
    [BsonId] public required string ConnectionId { get; set; }
    public required string UserId { get; set; }
    public required string TeamId { get; set; }
    public required string TeamName { get; set; }
    public required string SlackUserId { get; set; }
    public bool Write { get; set; }
    public string Scopes { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>How the user wants Slack to behave in Daksha.</summary>
[BsonIgnoreExtraElements]
public sealed class SlackUserSettings
{
    [BsonId] public required string UserId { get; set; }
    public int DashboardCount { get; set; } = 5;
    /// <summary>all | dms | mentions</summary>
    public string DashboardSource { get; set; } = "all";
    public bool MarkReadOnOpen { get; set; } = true;
    public bool NotifyDms { get; set; } = true;
    public bool NotifyMentions { get; set; } = true;
    public bool HideBots { get; set; }
    public string? DefaultConnectionId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A DM or mention that arrived through the Events API. Expires after two weeks.</summary>
[BsonIgnoreExtraElements]
public sealed class SlackActivity
{
    /// <summary>connectionId:eventId, so a redelivered event is stored once.</summary>
    [BsonId] public required string Id { get; set; }
    public required string ConnectionId { get; set; }
    public required string UserId { get; set; }
    public required string ChannelId { get; set; }
    public string ChannelType { get; set; } = "";
    public required string Kind { get; set; }
    public required string FromSlackUser { get; set; }
    public string Text { get; set; } = "";
    public string Ts { get; set; } = "";
    public bool Read { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class SlackOAuthAttempt
{
    [BsonId] public required string StateHash { get; set; }
    public required string UserId { get; set; }
    public required string FrontendUrl { get; set; }
    public bool Write { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class SlackException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

public static class SlackScopes
{
    /// <summary>Read-only: see channels, DMs and history, and search. Daksha acts as you, so it sees only what you can.</summary>
    public static readonly string[] Read =
    [
        "channels:read", "groups:read", "im:read", "mpim:read",
        "channels:history", "groups:history", "im:history", "mpim:history",
        "users:read", "search:read",
    ];
    /// <summary>Full access adds posting, opening a DM and marking conversations read.</summary>
    public static readonly string[] Write = ["chat:write", "im:write", "channels:write", "groups:write", "mpim:write"];

    public static string UserScope(bool write) => string.Join(',', write ? [.. Read, .. Write] : Read);
    public static bool CanWrite(string scopes) => scopes.Split(',', ' ').Contains("chat:write");
}

/// <summary>Turns Slack's message markup (&lt;@U1&gt;, &lt;#C1|general&gt;, &lt;https://x|label&gt;, &amp;amp;) into plain text.</summary>
public static partial class SlackText
{
    [GeneratedRegex(@"<@([UW][A-Z0-9]+)(?:\|[^>]*)?>")] private static partial Regex UserMention();
    [GeneratedRegex(@"<#[CG][A-Z0-9]+\|([^>]+)>")] private static partial Regex ChannelMention();
    [GeneratedRegex(@"<(https?://[^>|]+)\|([^>]+)>")] private static partial Regex LabelledLink();
    [GeneratedRegex(@"<(https?://[^>|]+)>")] private static partial Regex BareLink();
    [GeneratedRegex(@"<!(here|channel|everyone)(?:\|[^>]*)?>")] private static partial Regex Broadcast();

    public static string Clean(string? text, Func<string, string?> userName)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = UserMention().Replace(text, m => "@" + (userName(m.Groups[1].Value) ?? m.Groups[1].Value));
        s = ChannelMention().Replace(s, m => "#" + m.Groups[1].Value);
        s = LabelledLink().Replace(s, m => $"{m.Groups[2].Value} ({m.Groups[1].Value})");
        s = BareLink().Replace(s, m => m.Groups[1].Value);
        s = Broadcast().Replace(s, m => "@" + m.Groups[1].Value);
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }
}

public static class SlackIntegrationModule
{
    public const string IntegrationKey = "slack";
    private const string CredentialKey = "slack-user-token";
    private static readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Expires)> UserNames = new();

    public static IServiceCollection AddSlackIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("slack-api", c => c.BaseAddress = new Uri("https://slack.com/api/"));
        return services;
    }

    public static async Task EnsureSlackIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        await db.GetCollection<SlackOAuthAttempt>("slackOAuthAttempts").Indexes.CreateOneAsync(
            new CreateIndexModel<SlackOAuthAttempt>(Builders<SlackOAuthAttempt>.IndexKeys.Ascending(a => a.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_slackOAuthAttempts_expiresAtUtc" }), cancellationToken: ct);
        await db.GetCollection<SlackAccountDoc>("slackAccounts").Indexes.CreateOneAsync(
            new CreateIndexModel<SlackAccountDoc>(Builders<SlackAccountDoc>.IndexKeys.Ascending(a => a.UserId), new CreateIndexOptions { Name = "ix_slackAccounts_userId" }), cancellationToken: ct);
        await db.GetCollection<SlackAccountDoc>("slackAccounts").Indexes.CreateOneAsync(
            new CreateIndexModel<SlackAccountDoc>(Builders<SlackAccountDoc>.IndexKeys.Ascending(a => a.TeamId), new CreateIndexOptions { Name = "ix_slackAccounts_teamId" }), cancellationToken: ct);
        var activity = db.GetCollection<SlackActivity>("slackActivity");
        await activity.Indexes.CreateOneAsync(new CreateIndexModel<SlackActivity>(Builders<SlackActivity>.IndexKeys.Ascending(a => a.CreatedAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(14), Name = "ttl_slackActivity_createdAtUtc" }), cancellationToken: ct);
        await activity.Indexes.CreateOneAsync(new CreateIndexModel<SlackActivity>(Builders<SlackActivity>.IndexKeys.Ascending(a => a.UserId).Descending(a => a.CreatedAtUtc),
            new CreateIndexOptions { Name = "ix_slackActivity_user_created" }), cancellationToken: ct);
    }

    public static void MapSlackIntegration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/slack").WithTags("Slack");
        g.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();
        g.MapPost("/events", ReceiveEventAsync).AllowAnonymous();

        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapGet("/status", StatusAsync);
        a.MapPost("/connect", StartOAuthAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapPost("/write", SetWriteAsync);
        a.MapGet("/settings", GetSettingsAsync);
        a.MapPut("/settings", UpdateSettingsAsync);
        a.MapGet("/channels", ListChannelsAsync);
        a.MapGet("/channels/{channelId}/messages", HistoryAsync);
        a.MapGet("/channels/{channelId}/replies", RepliesAsync);
        a.MapPost("/channels/{channelId}/messages", SendAsync);
        a.MapPost("/channels/{channelId}/read", MarkReadAsync);
        a.MapGet("/search", SearchAsync);
        a.MapGet("/dashboard", DashboardAsync);
        a.MapPost("/activity/read", MarkActivityReadAsync);
    }

    // ---- configuration and OAuth --------------------------------------------------------------------

    public sealed record Settings(string ClientId, string ClientSecret, string RedirectUri, string FrontendUrl);

    private static bool IsAllowedUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static bool TryGetSettings(IConfiguration config, out Settings settings)
    {
        var id = config["Slack:OAuth:ClientId"];
        var secret = config["Slack:OAuth:ClientSecret"];
        var redirect = config["Slack:OAuth:RedirectUri"] ?? "http://localhost:5080/api/integrations/slack/callback";
        var frontend = config["Slack:OAuth:FrontendUrl"] ?? "http://localhost:3000/apps/slack";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) || !IsAllowedUri(redirect) || !IsAllowedUri(frontend)) { settings = null!; return false; }
        settings = new Settings(id, secret, redirect, frontend);
        return true;
    }

    public static string BuildAuthorizationUrl(Settings s, string state, bool write) =>
        QueryHelpers.AddQueryString("https://slack.com/oauth/v2/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = s.ClientId, ["user_scope"] = SlackScopes.UserScope(write), ["redirect_uri"] = s.RedirectUri, ["state"] = state,
        });

    private static IResult Back(string frontendUrl, string value) => Results.Redirect(QueryHelpers.AddQueryString(frontendUrl, new Dictionary<string, string?> { [value == "connected" ? "slack" : "error"] = value }));
    private static string HashState(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string NewState() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<IResult> StartOAuthAsync(bool? write, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        if (!TryGetSettings(config, out var settings))
            return Results.Problem(statusCode: 503, title: "Slack sign-in is not configured.", detail: "Set Slack:OAuth:ClientId and Slack:OAuth:ClientSecret in the API environment. See docs/integrations/slack.md.");
        var state = NewState();
        var now = DateTime.UtcNow;
        // Full access, once granted for any of the user's workspaces, is asked for again on reconnect so it is not lost.
        var full = write == true;
        await db.GetCollection<SlackOAuthAttempt>("slackOAuthAttempts").InsertOneAsync(new SlackOAuthAttempt
        {
            StateHash = HashState(state), UserId = who.UserId, FrontendUrl = settings.FrontendUrl, Write = full, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10),
        }, cancellationToken: ct);
        return Results.Ok(new SlackAuthorizationResponse(BuildAuthorizationUrl(settings, state, full)));
    }

    private static async Task<IResult> CompleteOAuthAsync(string? code, string? state, string? error, IMongoDatabase db, IConfiguration config, ISecretProtector protector,
        IHttpClientFactory clients, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Slack");
        if (string.IsNullOrWhiteSpace(state)) return Results.BadRequest("OAuth state is missing.");
        var now = DateTime.UtcNow;
        var attempt = await db.GetCollection<SlackOAuthAttempt>("slackOAuthAttempts").FindOneAndDeleteAsync(a => a.StateHash == HashState(state) && a.ExpiresAtUtc > now, cancellationToken: ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error)) return Back(attempt.FrontendUrl, "consent_denied");
        if (string.IsNullOrWhiteSpace(code)) return Back(attempt.FrontendUrl, "missing_code");
        if (!TryGetSettings(config, out var settings)) return Back(attempt.FrontendUrl, "oauth_not_configured");

        JsonDocument doc;
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret, ["code"] = code, ["redirect_uri"] = settings.RedirectUri,
            });
            using var response = await clients.CreateClient("slack-api").PostAsync("oauth.v2.access", form, ct);
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach Slack"); return Back(attempt.FrontendUrl, "slack_unavailable"); }
        catch (JsonException) { return Back(attempt.FrontendUrl, "token_exchange"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)) { logger.LogWarning("Slack code exchange failed: {Error}", Str(root, "error")); return Back(attempt.FrontendUrl, "token_exchange"); }
            if (!root.TryGetProperty("authed_user", out var user) || Str(user, "access_token") is not { Length: > 0 } token) return Back(attempt.FrontendUrl, "token_missing");
            if (Str(user, "refresh_token") is not null) return Back(attempt.FrontendUrl, "token_rotation");
            var teamId = root.TryGetProperty("team", out var team) ? Str(team, "id") ?? "" : "";
            var teamName = team.ValueKind == JsonValueKind.Object ? Str(team, "name") ?? teamId : teamId;
            var slackUser = Str(user, "id") ?? "";
            if (teamId.Length == 0 || slackUser.Length == 0) return Back(attempt.FrontendUrl, "profile_lookup");
            var scopes = Str(user, "scope") ?? "";

            var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
            var accounts = db.GetCollection<SlackAccountDoc>("slackAccounts");
            var existing = await accounts.Find(a => a.UserId == attempt.UserId && a.TeamId == teamId).FirstOrDefaultAsync(ct);
            var at = DateTime.UtcNow;
            string connectionId;
            if (existing is not null && await connections.Find(c => c.Id == existing.ConnectionId).AnyAsync(ct))
            {
                connectionId = existing.ConnectionId;
                await connections.UpdateOneAsync(c => c.Id == connectionId, Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connected)
                    .Set(c => c.DisplayName, teamName).Set(c => c.AccountIdentifier, teamName).Set(c => c.ConnectedAt, at).Set(c => c.DisconnectedAt, null).Set(c => c.LastError, null).Set(c => c.UpdatedAt, at), cancellationToken: ct);
            }
            else
            {
                var connection = new IntegrationConnection
                {
                    UserId = attempt.UserId, IntegrationKey = IntegrationKey, Status = ConnectionStatus.Connected, DisplayName = teamName,
                    AccountIdentifier = teamName, ProviderAccountId = teamId, ConnectedAt = at, CreatedAt = at, UpdatedAt = at,
                };
                await connections.InsertOneAsync(connection, cancellationToken: ct);
                connectionId = connection.Id;
            }
            await SaveTokenAsync(db, protector, connectionId, token, ct);
            await accounts.ReplaceOneAsync(a => a.ConnectionId == connectionId, new SlackAccountDoc
            {
                ConnectionId = connectionId, UserId = attempt.UserId, TeamId = teamId, TeamName = teamName, SlackUserId = slackUser,
                Write = SlackScopes.CanWrite(scopes), Scopes = scopes, UpdatedAtUtc = at,
            }, new ReplaceOptions { IsUpsert = true }, ct);
            return Back(attempt.FrontendUrl, "connected");
        }
    }

    private static async Task SaveTokenAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, string token, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationCredential>("integrationCredentials").UpdateOneAsync(
            Builders<IntegrationCredential>.Filter.Eq(c => c.ConnectionId, connectionId) & Builders<IntegrationCredential>.Filter.Eq(c => c.CredentialKey, CredentialKey),
            Builders<IntegrationCredential>.Update.Set(c => c.EncryptedValue, protector.Protect(token)).Set(c => c.EncryptionVersion, "v1")
                .SetOnInsert(c => c.ConnectionId, connectionId).SetOnInsert(c => c.CredentialKey, CredentialKey).SetOnInsert(c => c.CreatedAt, now).Set(c => c.UpdatedAt, now),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    // ---- status, disconnect, write ------------------------------------------------------------------

    internal static async Task<List<SlackAccountDoc>> LoadAccountsAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var accounts = await db.GetCollection<SlackAccountDoc>("slackAccounts").Find(a => a.UserId == userId).SortBy(a => a.TeamName).ToListAsync(ct);
        if (accounts.Count == 0) return accounts;
        var ids = accounts.Select(a => a.ConnectionId).ToList();
        var live = (await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => ids.Contains(c.Id) && c.Status == ConnectionStatus.Connected).Project(c => c.Id).ToListAsync(ct)).ToHashSet();
        return accounts.Where(a => live.Contains(a.ConnectionId)).ToList();
    }

    private static async Task<SlackAccountDoc?> PickAccountAsync(IMongoDatabase db, string userId, string? connectionId, CancellationToken ct)
    {
        var accounts = await LoadAccountsAsync(db, userId, ct);
        if (connectionId is not null) return accounts.FirstOrDefault(a => a.ConnectionId == connectionId);
        var s = await LoadSettingsAsync(db, userId, ct);
        return accounts.FirstOrDefault(a => a.ConnectionId == s.DefaultConnectionId) ?? accounts.FirstOrDefault();
    }

    private static async Task<IResult> StatusAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        var accounts = await LoadAccountsAsync(db, who.UserId, ct);
        var unread = await db.GetCollection<SlackActivity>("slackActivity").Find(a => a.UserId == who.UserId && !a.Read).ToListAsync(ct);
        var workspaces = accounts.Select(a => new SlackWorkspaceDto(a.ConnectionId, a.TeamId, a.TeamName, a.SlackUserId, a.Write, unread.Count(u => u.ConnectionId == a.ConnectionId))).ToArray();
        return Results.Ok(new SlackStatusDto(TryGetSettings(config, out _), !string.IsNullOrWhiteSpace(config["Slack:SigningSecret"]), workspaces.Length > 0, workspaces, null));
    }

    private static async Task<IResult> DisconnectAsync(string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var accounts = await LoadAccountsAsync(db, who.UserId, ct);
        var targets = connectionId is null ? accounts : accounts.Where(a => a.ConnectionId == connectionId).ToList();
        foreach (var a in targets)
        {
            // Revoke the token at Slack first (best effort), so disconnecting really cuts access.
            try
            {
                var token = await TokenAsync(db, protector, a.ConnectionId, ct);
                if (token is not null) { using var req = new HttpRequestMessage(HttpMethod.Post, "auth.revoke"); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); using var _ = await clients.CreateClient("slack-api").SendAsync(req, ct); }
            }
            catch (Exception ex) when (ex is HttpRequestException or CryptographicException or TaskCanceledException) { }
            var now = DateTime.UtcNow;
            await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == a.ConnectionId && c.UserId == who.UserId,
                Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Disconnected).Set(c => c.DisconnectedAt, now).Set(c => c.UpdatedAt, now), cancellationToken: ct);
            await db.GetCollection<IntegrationCredential>("integrationCredentials").DeleteManyAsync(c => c.ConnectionId == a.ConnectionId && c.CredentialKey == CredentialKey, ct);
            await db.GetCollection<SlackAccountDoc>("slackAccounts").DeleteOneAsync(x => x.ConnectionId == a.ConnectionId, ct);
            await db.GetCollection<UserDashboardApp>("userDashboardApps").DeleteManyAsync(x => x.UserId == who.UserId && x.ConnectionId == a.ConnectionId, ct);
            await db.GetCollection<SlackActivity>("slackActivity").DeleteManyAsync(x => x.ConnectionId == a.ConnectionId, ct);
        }
        return Results.NoContent();
    }

    /// <summary>Full access off is immediate (Daksha stops posting). Turning it on goes through Connect so Slack can ask for the extra permissions.</summary>
    private static async Task<IResult> SetWriteAsync(SetSlackWriteRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (r.Enabled) return Results.Problem(statusCode: 400, title: "Use Connect with full access to turn this on.");
        var accounts = await LoadAccountsAsync(db, who.UserId, ct);
        var ids = (r.ConnectionId is null ? accounts : accounts.Where(a => a.ConnectionId == r.ConnectionId)).Select(a => a.ConnectionId).ToList();
        if (ids.Count == 0) return Results.Problem(statusCode: 409, title: "Slack is not connected.");
        await db.GetCollection<SlackAccountDoc>("slackAccounts").UpdateManyAsync(a => ids.Contains(a.ConnectionId), Builders<SlackAccountDoc>.Update.Set(a => a.Write, false).Set(a => a.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
        return Results.NoContent();
    }

    // ---- settings -----------------------------------------------------------------------------------

    internal static async Task<SlackUserSettings> LoadSettingsAsync(IMongoDatabase db, string userId, CancellationToken ct) =>
        await db.GetCollection<SlackUserSettings>("slackSettings").Find(x => x.UserId == userId).FirstOrDefaultAsync(ct) ?? new SlackUserSettings { UserId = userId };

    private static async Task<SlackSettingsDto> ToDtoAsync(IMongoDatabase db, string userId, SlackUserSettings s, CancellationToken ct)
    {
        var accounts = await LoadAccountsAsync(db, userId, ct);
        var ids = accounts.Select(a => a.ConnectionId).ToList();
        var onDashboard = ids.Count > 0 && await db.GetCollection<UserDashboardApp>("userDashboardApps").Find(a => a.UserId == userId && ids.Contains(a.ConnectionId)).AnyAsync(ct);
        return new SlackSettingsDto(s.DashboardCount, s.DashboardSource, s.MarkReadOnOpen, s.NotifyDms, s.NotifyMentions, s.HideBots, s.DefaultConnectionId, onDashboard);
    }

    private static async Task<IResult> GetSettingsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        Results.Ok(await ToDtoAsync(db, who.UserId, await LoadSettingsAsync(db, who.UserId, ct), ct));

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<IResult> UpdateSettingsAsync(UpdateSlackSettingsRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (r.DashboardCount is { } n && (n < 3 || n > 10)) return Invalid("dashboardCount", "Choose between 3 and 10.");
        if (r.DashboardSource is not null && r.DashboardSource is not ("all" or "dms" or "mentions")) return Invalid("dashboardSource", "Unknown source.");
        var accounts = await LoadAccountsAsync(db, who.UserId, ct);
        if (r.DefaultConnectionId is { Length: > 0 } d && accounts.All(a => a.ConnectionId != d)) return Invalid("defaultConnectionId", "That workspace is not connected.");
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        if (r.DashboardCount is { } c) s.DashboardCount = c;
        if (r.DashboardSource is not null) s.DashboardSource = r.DashboardSource;
        if (r.MarkReadOnOpen is { } m) s.MarkReadOnOpen = m;
        if (r.NotifyDms is { } nd) s.NotifyDms = nd;
        if (r.NotifyMentions is { } nm) s.NotifyMentions = nm;
        if (r.HideBots is { } hb) s.HideBots = hb;
        if (r.DefaultConnectionId is not null) s.DefaultConnectionId = r.DefaultConnectionId.Length == 0 ? null : r.DefaultConnectionId;
        s.UpdatedAtUtc = DateTime.UtcNow;
        await db.GetCollection<SlackUserSettings>("slackSettings").ReplaceOneAsync(x => x.UserId == who.UserId, s, new ReplaceOptions { IsUpsert = true }, ct);
        return Results.Ok(await ToDtoAsync(db, who.UserId, s, ct));
    }

    // ---- calling Slack ------------------------------------------------------------------------------

    private sealed record Ctx(SlackAccountDoc Account, string Token, IMongoDatabase Db, IHttpClientFactory Clients);

    private static async Task<string?> TokenAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, CancellationToken ct)
    {
        var credential = await db.GetCollection<IntegrationCredential>("integrationCredentials").Find(c => c.ConnectionId == connectionId && c.CredentialKey == CredentialKey).FirstOrDefaultAsync(ct);
        return credential is null ? null : protector.Unprotect(credential.EncryptedValue);
    }

    private static async Task<Ctx> RequireAsync(string userId, string? connectionId, bool write, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await PickAccountAsync(db, userId, connectionId, ct) ?? throw new SlackException(409, "Connect a Slack workspace to continue.", "not_connected");
        if (write && !account.Write) throw new SlackException(403, "Daksha has read-only access to Slack. Turn on full access in Manage to post messages.", "write_required");
        string? token;
        try { token = await TokenAsync(db, protector, account.ConnectionId, ct); }
        catch (CryptographicException) { throw new SlackException(500, "Stored Slack credentials could not be decrypted."); }
        return new Ctx(account, token ?? throw new SlackException(409, "Slack credentials are missing. Reconnect your workspace.", "not_connected"), db, clients);
    }

    /// <summary>Calls a Slack Web API method as the user. Throws <see cref="SlackException"/> with a plain message for Slack's error codes.</summary>
    private static async Task<JsonDocument> SlackAsync(Ctx c, string method, Dictionary<string, string?>? query, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var url = query is null ? method : QueryHelpers.AddQueryString(method, query);
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await c.Clients.CreateClient("slack-api").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (attempt < 2 && wait <= TimeSpan.FromSeconds(20)) { await Task.Delay(wait, ct); continue; }
                throw new SlackException(429, "Slack is rate limiting requests. Please try again in a moment.", "rate_limited");
            }
            if (!response.IsSuccessStatusCode) throw new SlackException(502, "Slack could not complete the request.");
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return doc;
            var error = Str(doc.RootElement, "error") ?? "unknown_error";
            doc.Dispose();
            if (error is "invalid_auth" or "token_revoked" or "token_expired" or "account_inactive" or "not_authed")
            {
                await c.Db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(x => x.Id == c.Account.ConnectionId,
                    Builders<IntegrationConnection>.Update.Set(x => x.LastError, "Slack access was revoked. Reconnect your workspace.").Set(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
                throw new SlackException(409, "Slack access was revoked or has expired. Reconnect your workspace.", "reconnect_required");
            }
            throw error switch
            {
                "missing_scope" => new SlackException(403, "Slack has not approved that permission yet. Reconnect with full access.", "write_required"),
                "channel_not_found" or "not_in_channel" => new SlackException(404, "That conversation was not found, or you are not a member.", error),
                "is_archived" => new SlackException(409, "That channel is archived.", error),
                "msg_too_long" or "no_text" => new SlackException(400, error == "no_text" ? "Write a message first." : "That message is too long for Slack.", error),
                "ratelimited" => new SlackException(429, "Slack is rate limiting requests. Please try again in a moment.", "rate_limited"),
                _ => new SlackException(502, "Slack could not complete the request.", error),
            };
        }
    }

    private static IResult Fail(SlackException ex) =>
        Results.Problem(statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : 502, title: ex.Message, extensions: ex.Code is null ? null : new Dictionary<string, object?> { ["code"] = ex.Code });

    private static async Task<IResult> Guard(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (SlackException ex) { return Fail(ex); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Slack could not be reached. Please retry."); }
    }

    private static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Flag(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static readonly Regex ChannelIdPattern = new("^[CDGW][A-Z0-9]{6,20}$", RegexOptions.Compiled);
    private static readonly Regex TsPattern = new(@"^\d{1,12}\.\d{1,8}$", RegexOptions.Compiled);

    private static DateTimeOffset? FromTs(string? ts) => ts is not null && double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds((long)s) : null;

    /// <summary>Names for Slack user ids, looked up once per hour per person.</summary>
    private static async Task<Func<string, string?>> NamesAsync(Ctx c, IEnumerable<string> userIds, CancellationToken ct)
    {
        foreach (var id in userIds.Where(i => i.Length > 0).Distinct().Take(40))
        {
            var key = c.Account.TeamId + "|" + id;
            if (UserNames.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) continue;
            try
            {
                using var doc = await SlackAsync(c, "users.info", new() { ["user"] = id }, null, ct);
                var u = doc.RootElement.GetProperty("user");
                var profile = u.TryGetProperty("profile", out var p) ? p : default;
                var name = Str(profile, "display_name") is { Length: > 0 } d ? d : Str(profile, "real_name") is { Length: > 0 } r ? r : Str(u, "real_name") ?? Str(u, "name") ?? id;
                UserNames[key] = (name, DateTimeOffset.UtcNow.AddHours(1));
            }
            catch (SlackException) { UserNames[key] = (id, DateTimeOffset.UtcNow.AddMinutes(5)); }
        }
        return id => UserNames.TryGetValue(c.Account.TeamId + "|" + id, out var h) ? h.Name : null;
    }

    private static SlackMessageDto ToMessage(JsonElement m, Func<string, string?> names)
    {
        var user = Str(m, "user") ?? Str(m, "bot_id") ?? "";
        var isBot = m.TryGetProperty("bot_id", out _) || Str(m, "subtype") == "bot_message";
        var name = Str(m, "username") ?? names(user) ?? (user.Length > 0 ? user : "Slack");
        return new SlackMessageDto(Str(m, "ts") ?? "", user, name, SlackText.Clean(Str(m, "text"), names), Str(m, "thread_ts"),
            m.TryGetProperty("reply_count", out var rc) && rc.TryGetInt32(out var n) ? n : 0, FromTs(Str(m, "ts")), isBot, m.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0);
    }

    // ---- conversations ------------------------------------------------------------------------------

    private static Task<IResult> ListChannelsAsync(string? connectionId, string? kind, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        var c = await RequireAsync(who.UserId, connectionId, false, db, protector, clients, ct);
        var types = kind switch { "dms" => "im,mpim", "channels" => "public_channel,private_channel", _ => "public_channel,private_channel,mpim,im" };
        var all = new List<SlackChannelDto>();
        string? cursor = null;
        var idsToName = new List<string>();
        var raw = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            for (var page = 0; page < 5; page++)
            {
                var doc = await SlackAsync(c, "users.conversations", new() { ["types"] = types, ["exclude_archived"] = "true", ["limit"] = "200", ["cursor"] = cursor }, null, ct);
                docs.Add(doc);
                if (doc.RootElement.TryGetProperty("channels", out var chs)) foreach (var ch in chs.EnumerateArray()) raw.Add(ch);
                cursor = doc.RootElement.TryGetProperty("response_metadata", out var rm) ? Str(rm, "next_cursor") : null;
                if (string.IsNullOrEmpty(cursor)) break;
            }
            idsToName.AddRange(raw.Where(ch => Flag(ch, "is_im")).Select(ch => Str(ch, "user") ?? ""));
            var names = await NamesAsync(c, idsToName, ct);
            foreach (var ch in raw)
            {
                var isIm = Flag(ch, "is_im"); var isMpim = Flag(ch, "is_mpim");
                var userId = isIm ? Str(ch, "user") : null;
                var name = isIm ? (userId is null ? "Direct message" : names(userId) ?? userId) : (Str(ch, "name") ?? "");
                if (isMpim) name = name.Replace("mpdm-", "").Replace("--", ", ").TrimEnd('-', '1');
                all.Add(new SlackChannelDto(Str(ch, "id") ?? "", name, isIm ? "dm" : isMpim ? "group" : "channel", Flag(ch, "is_private") || isMpim,
                    ch.TryGetProperty("topic", out var t) ? Str(t, "value") : null, ch.TryGetProperty("num_members", out var nm) && nm.TryGetInt32(out var n) ? n : null, userId));
            }
        }
        finally { docs.ForEach(d => d.Dispose()); }
        return Results.Ok(all.OrderBy(x => x.Kind == "dm" ? 1 : 0).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    });

    private static Task<IResult> HistoryAsync(string channelId, string? connectionId, string? cursor, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ChannelIdPattern.IsMatch(channelId)) return Results.BadRequest("That conversation is not valid.");
        if (cursor is { Length: > 300 }) return Results.BadRequest("That page is not valid.");
        var c = await RequireAsync(who.UserId, connectionId, false, db, protector, clients, ct);
        var settings = await LoadSettingsAsync(db, who.UserId, ct);
        using var doc = await SlackAsync(c, "conversations.history", new() { ["channel"] = channelId, ["limit"] = "40", ["cursor"] = cursor }, null, ct);
        var items = doc.RootElement.TryGetProperty("messages", out var ms) ? ms.EnumerateArray().ToList() : [];
        var names = await NamesAsync(c, items.Select(m => Str(m, "user") ?? ""), ct);
        var messages = items.Select(m => ToMessage(m, names)).Where(m => !(settings.HideBots && m.IsBot)).Reverse().ToArray();
        if (settings.MarkReadOnOpen && c.Account.Write && messages.Length > 0 && cursor is null)
            _ = MarkAsync(c, channelId, messages[^1].Ts, ct); // best effort: opening a conversation in Daksha marks it read in Slack
        await db.GetCollection<SlackActivity>("slackActivity").UpdateManyAsync(a => a.ConnectionId == c.Account.ConnectionId && a.ChannelId == channelId, Builders<SlackActivity>.Update.Set(a => a.Read, true), cancellationToken: ct);
        var next = doc.RootElement.TryGetProperty("response_metadata", out var rm) ? Str(rm, "next_cursor") : null;
        return Results.Ok(new SlackMessagePage(messages, string.IsNullOrEmpty(next) ? null : next));
    });

    private static async Task MarkAsync(Ctx c, string channelId, string ts, CancellationToken ct)
    {
        try { using var _ = await SlackAsync(c, "conversations.mark", null, new { channel = channelId, ts }, ct); }
        catch (Exception ex) when (ex is SlackException or HttpRequestException) { }
    }

    private static Task<IResult> RepliesAsync(string channelId, string ts, string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ChannelIdPattern.IsMatch(channelId) || !TsPattern.IsMatch(ts)) return Results.BadRequest("That thread is not valid.");
        var c = await RequireAsync(who.UserId, connectionId, false, db, protector, clients, ct);
        using var doc = await SlackAsync(c, "conversations.replies", new() { ["channel"] = channelId, ["ts"] = ts, ["limit"] = "100" }, null, ct);
        var items = doc.RootElement.TryGetProperty("messages", out var ms) ? ms.EnumerateArray().ToList() : [];
        var names = await NamesAsync(c, items.Select(m => Str(m, "user") ?? ""), ct);
        return Results.Ok(new SlackMessagePage(items.Select(m => ToMessage(m, names)).ToArray(), null));
    });

    private static Task<IResult> SendAsync(string channelId, SendSlackMessageRequest r, string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ChannelIdPattern.IsMatch(channelId)) return Results.BadRequest("That conversation is not valid.");
        var text = r.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return Results.Problem(statusCode: 400, title: "Write a message first.");
        if (text.Length > 4000) return Results.Problem(statusCode: 400, title: "That message is too long (up to 4,000 characters).");
        if (r.ThreadTs is not null && !TsPattern.IsMatch(r.ThreadTs)) return Results.BadRequest("That thread is not valid.");
        var c = await RequireAsync(who.UserId, connectionId, true, db, protector, clients, ct);
        using var doc = await SlackAsync(c, "chat.postMessage", null, new { channel = channelId, text, thread_ts = r.ThreadTs }, ct);
        return Results.Ok(new { ts = Str(doc.RootElement, "ts") });
    });

    private static Task<IResult> MarkReadAsync(string channelId, string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ChannelIdPattern.IsMatch(channelId)) return Results.BadRequest("That conversation is not valid.");
        var c = await RequireAsync(who.UserId, connectionId, true, db, protector, clients, ct);
        using var history = await SlackAsync(c, "conversations.history", new() { ["channel"] = channelId, ["limit"] = "1" }, null, ct);
        var ts = history.RootElement.TryGetProperty("messages", out var ms) && ms.GetArrayLength() > 0 ? Str(ms[0], "ts") : null;
        if (ts is not null) using (await SlackAsync(c, "conversations.mark", null, new { channel = channelId, ts }, ct)) { }
        await db.GetCollection<SlackActivity>("slackActivity").UpdateManyAsync(a => a.ConnectionId == c.Account.ConnectionId && a.ChannelId == channelId, Builders<SlackActivity>.Update.Set(a => a.Read, true), cancellationToken: ct);
        return Results.NoContent();
    });

    private static Task<IResult> SearchAsync(string? q, string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Results.BadRequest("Enter something to search for (up to 200 characters).");
        var c = await RequireAsync(who.UserId, connectionId, false, db, protector, clients, ct);
        using var doc = await SlackAsync(c, "search.messages", new() { ["query"] = q.Trim(), ["count"] = "20", ["sort"] = "timestamp", ["sort_dir"] = "desc" }, null, ct);
        var hits = new List<SlackSearchHit>();
        if (doc.RootElement.TryGetProperty("messages", out var m) && m.TryGetProperty("matches", out var matches))
        {
            var list = matches.EnumerateArray().ToList();
            var names = await NamesAsync(c, list.Select(x => Str(x, "user") ?? ""), ct);
            foreach (var x in list)
            {
                var ch = x.TryGetProperty("channel", out var cc) ? cc : default;
                hits.Add(new SlackSearchHit(Str(ch, "id") ?? "", Str(ch, "name") ?? "", names(Str(x, "user") ?? "") ?? Str(x, "username") ?? "", SlackText.Clean(Str(x, "text"), names), Str(x, "ts") ?? "", Str(x, "permalink")));
            }
        }
        return Results.Ok(hits);
    });

    // ---- activity (Events API) and dashboard --------------------------------------------------------

    /// <summary>Checks Slack's request signature: HMAC-SHA256 of "v0:timestamp:body" with the app's signing secret, and a timestamp within five minutes.</summary>
    public static bool VerifySignature(string secret, string? timestamp, string? signature, string body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(now.ToUnixTimeSeconds() - ts) > 300) return false;
        var expected = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    private static async Task<IResult> ReceiveEventAsync(HttpRequest request, IMongoDatabase db, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var secret = config["Slack:SigningSecret"];
        if (string.IsNullOrWhiteSpace(secret)) return Results.Problem(statusCode: 503, title: "Slack events are not configured.");
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > 1_000_000 || !VerifySignature(secret, request.Headers["X-Slack-Request-Timestamp"], request.Headers["X-Slack-Signature"], body, DateTimeOffset.UtcNow))
            return Results.Unauthorized();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException) { return Results.BadRequest(); }
        using (doc)
        {
            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type == "url_verification") return Results.Ok(new { challenge = Str(root, "challenge") });
            if (type != "event_callback" || !root.TryGetProperty("event", out var ev)) return Results.Ok();
            if (Str(ev, "type") != "message" || Str(ev, "subtype") is { } sub && sub is not "thread_broadcast" || Str(ev, "bot_id") is not null) return Results.Ok();
            var teamId = Str(root, "team_id") ?? Str(ev, "team") ?? "";
            var from = Str(ev, "user") ?? "";
            var channel = Str(ev, "channel") ?? "";
            var channelType = Str(ev, "channel_type") ?? "";
            var text = Str(ev, "text") ?? "";
            var eventId = Str(root, "event_id") ?? Str(ev, "ts") ?? Guid.NewGuid().ToString("N");
            if (teamId.Length == 0 || from.Length == 0 || channel.Length == 0) return Results.Ok();

            var accounts = await db.GetCollection<SlackAccountDoc>("slackAccounts").Find(a => a.TeamId == teamId).ToListAsync(ct);
            foreach (var account in accounts)
            {
                if (account.SlackUserId == from) continue; // your own messages are not news
                var isDm = channelType is "im" or "mpim";
                var mention = text.Contains($"<@{account.SlackUserId}", StringComparison.Ordinal);
                if (!isDm && !mention) continue;
                try
                {
                    await db.GetCollection<SlackActivity>("slackActivity").InsertOneAsync(new SlackActivity
                    {
                        Id = $"{account.ConnectionId}:{eventId}", ConnectionId = account.ConnectionId, UserId = account.UserId, ChannelId = channel, ChannelType = channelType,
                        Kind = mention ? "mention" : "dm", FromSlackUser = from, Text = text.Length > 2000 ? text[..2000] : text, Ts = Str(ev, "ts") ?? "", CreatedAtUtc = DateTime.UtcNow,
                    }, cancellationToken: ct);
                }
                catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { } // Slack redelivered the same event
            }
            return Results.Ok();
        }
    }

    private static async Task<IResult> MarkActivityReadAsync(string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var filter = Builders<SlackActivity>.Filter.Eq(a => a.UserId, who.UserId);
        if (connectionId is not null) filter &= Builders<SlackActivity>.Filter.Eq(a => a.ConnectionId, connectionId);
        await db.GetCollection<SlackActivity>("slackActivity").UpdateManyAsync(filter, Builders<SlackActivity>.Update.Set(a => a.Read, true), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DashboardAsync(string? connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await PickAccountAsync(db, who.UserId, connectionId, ct);
        var events = !string.IsNullOrWhiteSpace(config["Slack:SigningSecret"]);
        if (account is null) return Results.Ok(new SlackDashboardDto(false, null, 0, [], [], events, []));
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        var errors = new List<string>();
        var rows = await db.GetCollection<SlackActivity>("slackActivity").Find(a => a.ConnectionId == account.ConnectionId).SortByDescending(a => a.CreatedAtUtc).Limit(60).ToListAsync(ct);
        var unread = rows.Count(a => !a.Read);
        var shown = rows.Where(a => s.DashboardSource == "all" || (s.DashboardSource == "dms" ? a.Kind == "dm" : a.Kind == "mention")).Take(s.DashboardCount).ToList();
        var activity = new List<SlackActivityDto>();
        IReadOnlyList<SlackChannelDto> conversations = [];
        try
        {
            var c = await RequireAsync(who.UserId, account.ConnectionId, false, db, protector, clients, ct);
            var names = await NamesAsync(c, shown.Select(a => a.FromSlackUser), ct);
            activity = shown.Select(a => new SlackActivityDto(a.Id, a.ChannelId, a.ChannelType is "im" ? "Direct message" : a.ChannelId, a.Kind, names(a.FromSlackUser) ?? a.FromSlackUser, SlackText.Clean(a.Text, names), new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAtUtc, DateTimeKind.Utc)), a.Read)).ToList();
            if (activity.Count == 0 && !events)
            {
                // Without the Events API there is no inbound feed, so show who you talk to instead.
                using var doc = await SlackAsync(c, "users.conversations", new() { ["types"] = "im,mpim,public_channel,private_channel", ["exclude_archived"] = "true", ["limit"] = "12" }, null, ct);
                var items = doc.RootElement.TryGetProperty("channels", out var chs) ? chs.EnumerateArray().ToList() : [];
                var dn = await NamesAsync(c, items.Where(x => Flag(x, "is_im")).Select(x => Str(x, "user") ?? ""), ct);
                conversations = items.Select(x => { var im = Flag(x, "is_im"); var u = im ? Str(x, "user") : null; return new SlackChannelDto(Str(x, "id") ?? "", im ? (u is null ? "Direct message" : dn(u) ?? u) : Str(x, "name") ?? "", im ? "dm" : Flag(x, "is_mpim") ? "group" : "channel", Flag(x, "is_private"), null, null, u); })
                    .Take(s.DashboardCount).ToArray();
            }
        }
        catch (SlackException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add("Slack could not be reached."); }
        return Results.Ok(new SlackDashboardDto(true, account.TeamName, unread, activity, conversations, events, errors));
    }

    // ---- for the assistant --------------------------------------------------------------------------

    /// <summary>Messages matching <paramref name="query"/> in the user's default Slack workspace, for the chat assistant. Null when Slack is not connected.</summary>
    internal static async Task<IReadOnlyList<SlackSearchHit>?> SearchForAssistantAsync(string userId, string query, int max, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(userId, null, false, db, protector, clients, ct);
            using var doc = await SlackAsync(c, "search.messages", new() { ["query"] = query, ["count"] = Math.Clamp(max, 1, 20).ToString(), ["sort"] = "timestamp", ["sort_dir"] = "desc" }, null, ct);
            var hits = new List<SlackSearchHit>();
            if (doc.RootElement.TryGetProperty("messages", out var m) && m.TryGetProperty("matches", out var matches))
            {
                var list = matches.EnumerateArray().ToList();
                var names = await NamesAsync(c, list.Select(x => Str(x, "user") ?? ""), ct);
                foreach (var x in list)
                {
                    var ch = x.TryGetProperty("channel", out var cc) ? cc : default;
                    hits.Add(new SlackSearchHit(Str(ch, "id") ?? "", Str(ch, "name") ?? "", names(Str(x, "user") ?? "") ?? Str(x, "username") ?? "", SlackText.Clean(Str(x, "text"), names), Str(x, "ts") ?? "", Str(x, "permalink")));
                }
            }
            return hits;
        }
        catch (SlackException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}
