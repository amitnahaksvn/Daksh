using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// One Microsoft sign-in covers Outlook mail, Outlook calendar, OneDrive and Azure DevOps. (Teams is deliberately not part of this.)
// One IntegrationConnection ("microsoft-outlook") per Microsoft account holds one encrypted refresh token. Microsoft Graph features
// share the Graph access token; Azure DevOps is a different resource, so its access token is obtained from the same refresh token.

public sealed record MicrosoftAuthorizationResponse(string AuthorizationUrl);
public sealed record MicrosoftStatusDto(bool Configured, bool Connected, string? ConnectionId, string? Email, string? DisplayName, IReadOnlyList<string> Features, string? Error, bool Write = false, string? DevOpsOrg = null, bool DevOpsToken = false);
public sealed record SetDevOpsTokenRequest(string? Organization, string? Token);
public sealed record MicrosoftSettingsDto(string Signature, bool ShowImages, int MailCount, string MailFilter, int CalendarDays, int CalendarCount, bool CalendarShowAllDay,
    int FileCount, string FileSource, int WorkItemCount, string WorkItemScope, bool WorkItemIncludeClosed, string? DevOpsDefaultOrg, bool ShowOnDashboard);
public sealed record UpdateMicrosoftSettingsRequest(string? Signature, bool? ShowImages, int? MailCount, string? MailFilter, int? CalendarDays, int? CalendarCount, bool? CalendarShowAllDay,
    int? FileCount, string? FileSource, int? WorkItemCount, string? WorkItemScope, bool? WorkItemIncludeClosed, string? DevOpsDefaultOrg);

/// <summary>How the user wants each Microsoft service to behave (mail reading/composing, what the dashboard tile shows, Azure DevOps scope). Defaults suit most people.</summary>
[BsonIgnoreExtraElements]
public sealed class MicrosoftUserSettings
{
    [BsonId] public required string UserId { get; set; }
    public string Signature { get; set; } = "";
    public bool ShowImages { get; set; }
    public int MailCount { get; set; } = 5;
    /// <summary>inbox | unread | flagged | important</summary>
    public string MailFilter { get; set; } = "inbox";
    public int CalendarDays { get; set; } = 7;
    public int CalendarCount { get; set; } = 6;
    public bool CalendarShowAllDay { get; set; } = true;
    public int FileCount { get; set; } = 5;
    /// <summary>recent | shared</summary>
    public string FileSource { get; set; } = "recent";
    public int WorkItemCount { get; set; } = 6;
    /// <summary>assigned | created</summary>
    public string WorkItemScope { get; set; } = "assigned";
    public bool WorkItemIncludeClosed { get; set; }
    public string? DevOpsDefaultOrg { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
public sealed record MicrosoftAdminApproval(string Link, string Message);
public sealed record SetMicrosoftWriteRequest(bool Enabled);
public sealed record OutlookMessageSummary(string Id, string From, string FromAddress, string Subject, string Preview, DateTimeOffset? Received, bool IsRead, bool HasAttachments);
public sealed record OutlookMessage(string Id, string From, string To, string Subject, DateTimeOffset? Received, string Body, bool IsHtml);
public sealed record OutlookMessagePage(IReadOnlyList<OutlookMessageSummary> Messages, int? NextSkip);
public sealed record SendOutlookMessageRequest(string? To, string? Subject, string? Body);
public sealed record MarkOutlookReadRequest(bool IsRead);
public sealed record DisableFeatureRequest(string? Feature);
public sealed record DriveItemDto(string Id, string Name, bool IsFolder, long Size, DateTimeOffset? Modified, string? WebUrl, string? MimeType);
public sealed record DriveListing(string? FolderId, string FolderName, IReadOnlyList<DriveItemDto> Items);
public sealed record DevOpsWorkItemDto(int Id, string Title, string State, string Type, string Project, string Url);
public sealed record DevOpsSummary(IReadOnlyList<string> Organizations, string? Organization, IReadOnlyList<string> Projects, IReadOnlyList<DevOpsWorkItemDto> WorkItems);

[BsonIgnoreExtraElements]
public sealed class MicrosoftAccountDoc
{
    [BsonId] public required string ConnectionId { get; set; }
    public required string UserId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Which of mail / calendar / onedrive / devops the user consented to.</summary>
    public List<string> Features { get; set; } = [];
    /// <summary>Full access (send mail, change events and files). Off = read-only, which needs far fewer permissions and is easier for a company to approve.</summary>
    public bool Write { get; set; }
    /// <summary>Azure DevOps through a personal access token (any account, including personal ones) instead of Microsoft sign-in. The token itself is stored encrypted as a credential.</summary>
    public bool DevOpsViaToken { get; set; }
    public string? DevOpsOrg { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MicrosoftOAuthAttempt
{
    [BsonId] public required string StateHash { get; set; }
    public required string UserId { get; set; }
    public required string ProtectedCodeVerifier { get; set; }
    public required string FrontendUrl { get; set; }
    public required List<string> Features { get; set; }
    public bool Write { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class MicrosoftException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

public static class MicrosoftFeatures
{
    public const string Mail = "mail", Calendar = "calendar", OneDrive = "onedrive", DevOps = "devops";
    public static readonly string[] All = [Mail, Calendar, OneDrive, DevOps];
    public const string GraphResource = "https://graph.microsoft.com/.default";
    public const string DevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private const string DevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/user_impersonation";
    private static readonly string[] Base = ["openid", "profile", "email", "offline_access", "User.Read"];

    /// <summary>Unknown names are dropped; nothing chosen means mail + calendar + OneDrive (Azure DevOps is opt-in).</summary>
    public static List<string> Parse(string? features)
    {
        var chosen = (features ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant()).Where(All.Contains).Distinct().ToList();
        return chosen.Count > 0 ? chosen : [Mail, Calendar, OneDrive];
    }

    /// <summary>Read-only by default. <paramref name="write"/> adds the permissions to send mail and change events and files.</summary>
    public static string ScopeFor(IEnumerable<string> features, bool write = false)
    {
        var scopes = new List<string>(Base);
        foreach (var f in features)
            switch (f)
            {
                case Mail: scopes.AddRange(write ? ["Mail.ReadWrite", "Mail.Send"] : ["Mail.Read"]); break;
                case Calendar: scopes.Add(write ? "Calendars.ReadWrite" : "Calendars.Read"); break;
                case OneDrive: scopes.Add(write ? "Files.ReadWrite" : "Files.Read"); break;
                case DevOps: scopes.Add(DevOpsScope); break;
            }
        return string.Join(' ', scopes);
    }

    /// <summary>The Microsoft Graph permissions an administrator approves for the whole company (Azure DevOps asks each person, so it is not part of this).</summary>
    public static string AdminScopeFor(IEnumerable<string> features, bool write)
    {
        var graph = ScopeFor(features.Where(f => f != DevOps), write).Split(' ')
            .Where(s => s is not ("openid" or "profile" or "email" or "offline_access"))
            .Select(s => "https://graph.microsoft.com/" + s);
        return string.Join(' ', graph);
    }
}

public static class MicrosoftIntegrationModule
{
    public const string IntegrationKey = "microsoft-outlook";
    private const string CredentialKey = "microsoft-oauth-token";
    private const string DevOpsTokenKey = "azure-devops-pat";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> TokenCache = new();

    public static IServiceCollection AddMicrosoftIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("ms-login", c => c.BaseAddress = new Uri("https://login.microsoftonline.com/"));
        services.AddHttpClient("ms-graph", c => c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
        services.AddHttpClient("ms-devops");
        return services;
    }

    public static async Task EnsureMicrosoftIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        await db.GetCollection<MicrosoftOAuthAttempt>("microsoftOAuthAttempts").Indexes.CreateOneAsync(
            new CreateIndexModel<MicrosoftOAuthAttempt>(Builders<MicrosoftOAuthAttempt>.IndexKeys.Ascending(a => a.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_microsoftOAuthAttempts_expiresAtUtc" }), cancellationToken: ct);
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").Indexes.CreateOneAsync(
            new CreateIndexModel<MicrosoftAccountDoc>(Builders<MicrosoftAccountDoc>.IndexKeys.Ascending(a => a.UserId), new CreateIndexOptions { Name = "ix_microsoftAccounts_userId" }), cancellationToken: ct);
    }

    public static void MapMicrosoftIntegration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/microsoft").WithTags("Microsoft");
        g.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();

        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapGet("/status", StatusAsync);
        a.MapPost("/connect", StartOAuthAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapPost("/features/disable", DisableFeatureAsync);
        a.MapPost("/write", SetWriteAsync);
        a.MapGet("/settings", GetSettingsAsync);
        a.MapPut("/settings", UpdateSettingsAsync);
        a.MapPost("/devops/token", SetDevOpsTokenAsync);
        a.MapDelete("/devops/token", RemoveDevOpsTokenAsync);
        a.MapGet("/admin-approval", AdminApprovalAsync);
        a.MapGet("/mail/messages", ListMailAsync);
        a.MapGet("/mail/messages/{id}", GetMailAsync);
        a.MapPost("/mail/messages", SendMailAsync);
        a.MapPatch("/mail/messages/{id}", MarkReadAsync);
        a.MapGet("/onedrive/files", ListFilesAsync);
        a.MapGet("/devops/summary", DevOpsSummaryAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- configuration and OAuth -------------------------------------------------------------------

    internal sealed record Settings(string ClientId, string? ClientSecret, string Tenant, string RedirectUri, string FrontendUrl);

    private static bool TryGetSettings(IConfiguration config, out Settings settings)
    {
        var clientId = config["Microsoft:OAuth:ClientId"];
        var redirect = config["Microsoft:OAuth:RedirectUri"] ?? "http://localhost:5080/api/integrations/microsoft/callback";
        var frontend = config["Microsoft:OAuth:FrontendUrl"] ?? "http://localhost:3000/apps/microsoft";
        if (string.IsNullOrWhiteSpace(clientId) || !IsAllowedUri(redirect) || !IsAllowedUri(frontend)) { settings = null!; return false; }
        var tenant = config["Microsoft:OAuth:Tenant"];
        settings = new Settings(clientId, config["Microsoft:OAuth:ClientSecret"], string.IsNullOrWhiteSpace(tenant) ? "common" : tenant, redirect, frontend);
        return true;
    }

    private static bool IsAllowedUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    internal static string BuildAuthorizationUrl(Settings s, string state, string challenge, string scope) =>
        QueryHelpers.AddQueryString($"https://login.microsoftonline.com/{s.Tenant}/oauth2/v2.0/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = s.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = s.RedirectUri,
            ["response_mode"] = "query",
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        });

    private static async Task<IResult> StartOAuthAsync(string? features, bool? write, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, ISecretProtector protector, CancellationToken ct)
    {
        if (!TryGetSettings(config, out var settings))
            return Results.Problem(statusCode: 503, title: "Microsoft sign-in is not configured.",
                detail: "Set Microsoft:OAuth:ClientId (and ClientSecret) in the API environment. See docs/integrations/microsoft.md.");
        var existing = await LoadAccountAsync(db, who.UserId, ct);
        var wanted = MicrosoftFeatures.Parse(features);
        // Reconnecting or adding a service keeps what was already granted, so the user is asked for the new permissions only.
        var all = existing is null ? wanted : existing.Features.Union(wanted).ToList();
        // Full access, once granted, stays on until the user turns it off.
        var full = write == true || existing?.Write == true;

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var now = DateTime.UtcNow;
        await db.GetCollection<MicrosoftOAuthAttempt>("microsoftOAuthAttempts").InsertOneAsync(new MicrosoftOAuthAttempt
        {
            StateHash = HashState(state), UserId = who.UserId, ProtectedCodeVerifier = protector.Protect(verifier),
            FrontendUrl = settings.FrontendUrl, Features = all, Write = full, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10),
        }, cancellationToken: ct);
        return Results.Ok(new MicrosoftAuthorizationResponse(BuildAuthorizationUrl(settings, state, challenge, MicrosoftFeatures.ScopeFor(all, full))));
    }

    private static async Task<IResult> CompleteOAuthAsync(string? code, string? state, string? error, string? admin_consent, IMongoDatabase db, IConfiguration config,
        ISecretProtector protector, IHttpClientFactory clients, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Microsoft");
        var adminConsent = admin_consent;
        // An administrator who followed the one-time approval link is sent back here (admin_consent=True), with no sign-in state to look up.
        if (adminConsent is not null)
        {
            if (!TryGetSettings(config, out var s)) return Results.BadRequest("Microsoft sign-in is not configured.");
            var origin = new Uri(s.FrontendUrl).GetLeftPart(UriPartial.Authority);
            return Results.Redirect($"{origin}/approved?status={(adminConsent.Equals("True", StringComparison.OrdinalIgnoreCase) ? "ok" : "denied")}");
        }
        if (string.IsNullOrWhiteSpace(state)) return Results.BadRequest("OAuth state is missing.");
        var now = DateTime.UtcNow;
        var attempt = await db.GetCollection<MicrosoftOAuthAttempt>("microsoftOAuthAttempts")
            .FindOneAndDeleteAsync(a => a.StateHash == HashState(state) && a.ExpiresAtUtc > now, cancellationToken: ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error)) return Back(attempt.FrontendUrl, "error", "consent_denied");
        if (string.IsNullOrWhiteSpace(code)) return Back(attempt.FrontendUrl, "error", "missing_code");
        if (!TryGetSettings(config, out var settings)) return Back(attempt.FrontendUrl, "error", "oauth_not_configured");

        string verifier;
        try { verifier = protector.Unprotect(attempt.ProtectedCodeVerifier); }
        catch (CryptographicException ex) { logger.LogError(ex, "Could not read Microsoft OAuth verifier for user {UserId}", attempt.UserId); return Back(attempt.FrontendUrl, "error", "server_config"); }

        TokenResponse? token;
        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", settings.ClientId), new("code", code), new("code_verifier", verifier),
                new("grant_type", "authorization_code"), new("redirect_uri", settings.RedirectUri),
                new("scope", MicrosoftFeatures.ScopeFor(attempt.Features, attempt.Write)),
            };
            if (!string.IsNullOrEmpty(settings.ClientSecret)) form.Add(new("client_secret", settings.ClientSecret));
            using var response = await clients.CreateClient("ms-login").PostAsync($"{settings.Tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Microsoft code exchange returned HTTP {Status}", (int)response.StatusCode);
                return Back(attempt.FrontendUrl, "error", "token_exchange");
            }
            token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach Microsoft sign-in"); return Back(attempt.FrontendUrl, "error", "microsoft_unavailable"); }
        if (string.IsNullOrWhiteSpace(token?.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken)) return Back(attempt.FrontendUrl, "error", "refresh_token_missing");

        string email, name;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "me?$select=displayName,mail,userPrincipalName");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var res = await clients.CreateClient("ms-graph").SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return Back(attempt.FrontendUrl, "error", "profile_lookup");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            string? Str(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            email = Str("mail") ?? Str("userPrincipalName") ?? "";
            name = Str("displayName") ?? email;
        }
        catch (HttpRequestException) { return Back(attempt.FrontendUrl, "error", "microsoft_unavailable"); }
        if (email.Length == 0) return Back(attempt.FrontendUrl, "error", "profile_lookup");

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var existing = await connections.Find(c => c.UserId == attempt.UserId && c.IntegrationKey == IntegrationKey && c.AccountIdentifier == email)
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        var at = DateTime.UtcNow;
        IntegrationConnection connection;
        if (existing is null)
        {
            connection = new IntegrationConnection
            {
                UserId = attempt.UserId, IntegrationKey = IntegrationKey, Status = ConnectionStatus.Connected, DisplayName = name,
                AccountIdentifier = email, ProviderAccountId = email, ConnectedAt = at, CreatedAt = at, UpdatedAt = at,
            };
            await connections.InsertOneAsync(connection, cancellationToken: ct);
        }
        else
        {
            connection = existing;
            await connections.UpdateOneAsync(c => c.Id == existing.Id, Builders<IntegrationConnection>.Update
                .Set(c => c.Status, ConnectionStatus.Connected).Set(c => c.DisplayName, name).Set(c => c.ConnectedAt, at)
                .Set(c => c.DisconnectedAt, null).Set(c => c.LastError, null).Set(c => c.UpdatedAt, at), cancellationToken: ct);
        }

        await SaveTokenAsync(db, protector, connection.Id, token.RefreshToken, ct);
        TokenCache.Keys.Where(k => k.StartsWith(connection.Id + "|", StringComparison.Ordinal)).ToList().ForEach(k => TokenCache.TryRemove(k, out _));
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").ReplaceOneAsync(a => a.ConnectionId == connection.Id,
            new MicrosoftAccountDoc { ConnectionId = connection.Id, UserId = attempt.UserId, Email = email, DisplayName = name, Features = attempt.Features, Write = attempt.Write, UpdatedAtUtc = at },
            new ReplaceOptions { IsUpsert = true }, ct);
        return Back(attempt.FrontendUrl, "microsoft", "connected");
    }

    private static async Task<IResult> StatusAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        var configured = TryGetSettings(config, out _);
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.Ok(new MicrosoftStatusDto(configured, false, null, null, null, [], null));
        var connection = await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == account.ConnectionId).FirstOrDefaultAsync(ct);
        var ok = connection?.Status == ConnectionStatus.Connected;
        return Results.Ok(new MicrosoftStatusDto(configured, ok, account.ConnectionId, account.Email, account.DisplayName, ok ? account.Features : [], ok ? null : connection?.LastError, ok && account.Write, ok ? account.DevOpsOrg : null, ok && account.DevOpsViaToken));
    }

    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.NoContent();
        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == account.ConnectionId && c.UserId == who.UserId,
            Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Disconnected).Set(c => c.DisconnectedAt, now).Set(c => c.UpdatedAt, now), cancellationToken: ct);
        await db.GetCollection<IntegrationCredential>("integrationCredentials").DeleteManyAsync(c => c.ConnectionId == account.ConnectionId && c.CredentialKey == CredentialKey, ct);
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").DeleteOneAsync(a => a.ConnectionId == account.ConnectionId, ct);
        await db.GetCollection<UserDashboardApp>("userDashboardApps").DeleteManyAsync(a => a.UserId == who.UserId && a.ConnectionId == account.ConnectionId, ct);
        TokenCache.Keys.Where(k => k.StartsWith(account.ConnectionId + "|", StringComparison.Ordinal)).ToList().ForEach(k => TokenCache.TryRemove(k, out _));
        return Results.NoContent();
    }

    /// <summary>Turns one service off in Daksha. (Turning it back on goes through Connect, which skips Microsoft's consent screen when it was already granted.)</summary>
    private static async Task<IResult> DisableFeatureAsync(DisableFeatureRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (req.Feature is null || !MicrosoftFeatures.All.Contains(req.Feature)) return Results.Problem(statusCode: 400, title: "Unknown Microsoft service.");
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.Problem(statusCode: 409, title: "Microsoft is not connected.");
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").UpdateOneAsync(a => a.ConnectionId == account.ConnectionId,
            Builders<MicrosoftAccountDoc>.Update.Pull(a => a.Features, req.Feature).Set(a => a.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
        if (req.Feature == MicrosoftFeatures.DevOps) await ClearDevOpsTokenAsync(db, account.ConnectionId, ct);
        return Results.NoContent();
    }

    private static async Task ClearDevOpsTokenAsync(IMongoDatabase db, string connectionId, CancellationToken ct)
    {
        await db.GetCollection<IntegrationCredential>("integrationCredentials").DeleteManyAsync(c => c.ConnectionId == connectionId && c.CredentialKey == DevOpsTokenKey, ct);
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").UpdateOneAsync(a => a.ConnectionId == connectionId,
            Builders<MicrosoftAccountDoc>.Update.Set(a => a.DevOpsViaToken, false).Set(a => a.DevOpsOrg, null), cancellationToken: ct);
    }

    private static bool ValidOrg(string? org) => !string.IsNullOrWhiteSpace(org) && org.Length <= 100 && org.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');

    private static string BasicAuth(string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token));

    /// <summary>Connects Azure DevOps with a personal access token. The token is checked against the organization first, then stored encrypted; it is never returned.</summary>
    private static async Task<IResult> SetDevOpsTokenAsync(SetDevOpsTokenRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var org = req.Organization?.Trim();
        var token = req.Token?.Trim();
        if (!ValidOrg(org)) return Results.Problem(statusCode: 400, title: "Enter your Azure DevOps organization name (the part after dev.azure.com/).");
        if (string.IsNullOrEmpty(token) || token.Length is < 20 or > 200 || token.Any(char.IsWhiteSpace)) return Results.Problem(statusCode: 400, title: "Paste the personal access token exactly as Azure DevOps showed it.");
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.Problem(statusCode: 409, title: "Connect your Microsoft account first.", extensions: new Dictionary<string, object?> { ["code"] = "not_connected" });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://dev.azure.com/{org}/_apis/projects?$top=1&api-version=7.1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(token!));
            using var response = await clients.CreateClient("ms-devops").SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
                return Results.Problem(statusCode: 400, title: "Azure DevOps did not accept that token. Check it has not expired and has Work Items: Read (and Project and Team: Read).");
            if (response.StatusCode == HttpStatusCode.NotFound) return Results.Problem(statusCode: 400, title: $"Azure DevOps organization \"{org}\" was not found.");
            // A token for the wrong account is answered with a sign-in page (HTML, 200) rather than an error.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
                return Results.Problem(statusCode: 400, title: "Azure DevOps did not accept that token for this organization.");
        }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Azure DevOps could not be reached. Please retry."); }

        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationCredential>("integrationCredentials").UpdateOneAsync(
            Builders<IntegrationCredential>.Filter.Eq(c => c.ConnectionId, account.ConnectionId) & Builders<IntegrationCredential>.Filter.Eq(c => c.CredentialKey, DevOpsTokenKey),
            Builders<IntegrationCredential>.Update.Set(c => c.EncryptedValue, protector.Protect(token!)).Set(c => c.EncryptionVersion, "v1")
                .SetOnInsert(c => c.ConnectionId, account.ConnectionId).SetOnInsert(c => c.CredentialKey, DevOpsTokenKey).SetOnInsert(c => c.CreatedAt, now).Set(c => c.UpdatedAt, now),
            new UpdateOptions { IsUpsert = true }, ct);
        var features = account.Features.Contains(MicrosoftFeatures.DevOps) ? account.Features : [.. account.Features, MicrosoftFeatures.DevOps];
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").UpdateOneAsync(a => a.ConnectionId == account.ConnectionId,
            Builders<MicrosoftAccountDoc>.Update.Set(a => a.DevOpsViaToken, true).Set(a => a.DevOpsOrg, org).Set(a => a.Features, features).Set(a => a.UpdatedAtUtc, now), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveDevOpsTokenAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.NoContent();
        await ClearDevOpsTokenAsync(db, account.ConnectionId, ct);
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").UpdateOneAsync(a => a.ConnectionId == account.ConnectionId,
            Builders<MicrosoftAccountDoc>.Update.Pull(a => a.Features, MicrosoftFeatures.DevOps).Set(a => a.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>Turns full access off (back to read-only) in Daksha. Turning it on goes through Connect so Microsoft can ask for the extra permissions.</summary>
    private static async Task<IResult> SetWriteAsync(SetMicrosoftWriteRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (req.Enabled) return Results.Problem(statusCode: 400, title: "Use Connect with full access to turn this on.");
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.Problem(statusCode: 409, title: "Microsoft is not connected.");
        await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").UpdateOneAsync(a => a.ConnectionId == account.ConnectionId,
            Builders<MicrosoftAccountDoc>.Update.Set(a => a.Write, false).Set(a => a.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>A one-time approval link for a company's IT administrator, plus a short message to send with it. Approving once lets everyone at that company use Daksha.</summary>
    private static IResult AdminApprovalAsync(string? domain, string? features, bool? write, IConfiguration config)
    {
        if (!TryGetSettings(config, out var s)) return Results.Problem(statusCode: 503, title: "Microsoft sign-in is not configured.");
        var d = (domain ?? "").Trim().ToLowerInvariant();
        if (d.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(d, @"^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?$") ) return Results.Problem(statusCode: 400, title: "Enter a company domain such as example.com.");
        var chosen = MicrosoftFeatures.Parse(features);
        var link = QueryHelpers.AddQueryString($"https://login.microsoftonline.com/{(d.Length > 0 ? d : "organizations")}/v2.0/adminconsent", new Dictionary<string, string?>
        {
            ["client_id"] = s.ClientId,
            ["scope"] = MicrosoftFeatures.AdminScopeFor(chosen, write == true),
            ["redirect_uri"] = s.RedirectUri,
        });
        var access = write == true ? "read and manage" : "read";
        var names = chosen.Where(f => f != MicrosoftFeatures.DevOps).Select(f => f switch { MicrosoftFeatures.Mail => "Outlook mail", MicrosoftFeatures.Calendar => "calendar", _ => "OneDrive files" }).ToList();
        var services = names.Count == 0 ? "Microsoft account" : names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
        var message = $"Hi, our team wants to use Daksha, which needs to {access} their {services} on behalf of each person who turns it on. " +
                      "Please open this link as a Microsoft Entra administrator and click Accept once. After that, everyone here can sign in to Daksha without further approval:\n" + link;
        return Results.Ok(new MicrosoftAdminApproval(link, message));
    }

    // ---- settings ----------------------------------------------------------------------------------

    internal static async Task<MicrosoftUserSettings> LoadSettingsAsync(IMongoDatabase db, string userId, CancellationToken ct) =>
        await db.GetCollection<MicrosoftUserSettings>("microsoftSettings").Find(x => x.UserId == userId).FirstOrDefaultAsync(ct) ?? new MicrosoftUserSettings { UserId = userId };

    private static async Task<MicrosoftSettingsDto> ToSettingsDtoAsync(IMongoDatabase db, string userId, MicrosoftUserSettings s, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, ct);
        var onDashboard = account is not null && await db.GetCollection<UserDashboardApp>("userDashboardApps").Find(a => a.UserId == userId && a.ConnectionId == account.ConnectionId).AnyAsync(ct);
        return new MicrosoftSettingsDto(s.Signature, s.ShowImages, s.MailCount, s.MailFilter, s.CalendarDays, s.CalendarCount, s.CalendarShowAllDay, s.FileCount, s.FileSource,
            s.WorkItemCount, s.WorkItemScope, s.WorkItemIncludeClosed, s.DevOpsDefaultOrg, onDashboard);
    }

    private static async Task<IResult> GetSettingsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        Results.Ok(await ToSettingsDtoAsync(db, who.UserId, await LoadSettingsAsync(db, who.UserId, ct), ct));

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<IResult> UpdateSettingsAsync(UpdateMicrosoftSettingsRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (r.Signature is { Length: > 2000 }) return Invalid("signature", "The signature can be at most 2,000 characters.");
        foreach (var (v, name) in new[] { (r.MailCount, "mailCount"), (r.CalendarCount, "calendarCount"), (r.FileCount, "fileCount"), (r.WorkItemCount, "workItemCount") })
            if (v is { } n && (n < 3 || n > 10)) return Invalid(name, "Choose between 3 and 10.");
        if (r.CalendarDays is { } d && d is not (1 or 3 or 7 or 14)) return Invalid("calendarDays", "Choose 1, 3, 7 or 14 days.");
        if (r.MailFilter is not null && r.MailFilter is not ("inbox" or "unread" or "flagged" or "important")) return Invalid("mailFilter", "Unknown mail filter.");
        if (r.FileSource is not null && r.FileSource is not ("recent" or "shared")) return Invalid("fileSource", "Unknown file source.");
        if (r.WorkItemScope is not null && r.WorkItemScope is not ("assigned" or "created")) return Invalid("workItemScope", "Unknown work item scope.");
        if (r.DevOpsDefaultOrg is { Length: > 0 } org && !ValidOrg(org)) return Invalid("devOpsDefaultOrg", "That organization name is not valid.");

        var s = await LoadSettingsAsync(db, who.UserId, ct);
        if (r.Signature is not null) s.Signature = r.Signature.Trim();
        if (r.ShowImages is { } si) s.ShowImages = si;
        if (r.MailCount is { } mc) s.MailCount = mc;
        if (r.MailFilter is not null) s.MailFilter = r.MailFilter;
        if (r.CalendarDays is { } cd) s.CalendarDays = cd;
        if (r.CalendarCount is { } cc) s.CalendarCount = cc;
        if (r.CalendarShowAllDay is { } ad) s.CalendarShowAllDay = ad;
        if (r.FileCount is { } fc) s.FileCount = fc;
        if (r.FileSource is not null) s.FileSource = r.FileSource;
        if (r.WorkItemCount is { } wc) s.WorkItemCount = wc;
        if (r.WorkItemScope is not null) s.WorkItemScope = r.WorkItemScope;
        if (r.WorkItemIncludeClosed is { } ic) s.WorkItemIncludeClosed = ic;
        if (r.DevOpsDefaultOrg is not null) s.DevOpsDefaultOrg = r.DevOpsDefaultOrg.Length == 0 ? null : r.DevOpsDefaultOrg;
        s.UpdatedAtUtc = DateTime.UtcNow;
        await db.GetCollection<MicrosoftUserSettings>("microsoftSettings").ReplaceOneAsync(x => x.UserId == who.UserId, s, new ReplaceOptions { IsUpsert = true }, ct);
        return Results.Ok(await ToSettingsDtoAsync(db, who.UserId, s, ct));
    }

    // ---- accounts and tokens -----------------------------------------------------------------------

    /// <summary>The user's connected Microsoft account, or null.</summary>
    public static async Task<MicrosoftAccountDoc?> LoadAccountAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var accounts = await db.GetCollection<MicrosoftAccountDoc>("microsoftAccounts").Find(a => a.UserId == userId).SortByDescending(a => a.UpdatedAtUtc).ToListAsync(ct);
        foreach (var a in accounts)
            if (await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == a.ConnectionId && c.Status == ConnectionStatus.Connected).AnyAsync(ct)) return a;
        return accounts.FirstOrDefault();
    }

    private static Task SaveTokenAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, string refreshToken, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<IntegrationCredential>.Filter.Eq(c => c.ConnectionId, connectionId) & Builders<IntegrationCredential>.Filter.Eq(c => c.CredentialKey, CredentialKey);
        var update = Builders<IntegrationCredential>.Update
            .Set(c => c.EncryptedValue, protector.Protect(refreshToken)).Set(c => c.EncryptionVersion, "v1")
            .SetOnInsert(c => c.ConnectionId, connectionId).SetOnInsert(c => c.CredentialKey, CredentialKey)
            .SetOnInsert(c => c.CreatedAt, now).Set(c => c.UpdatedAt, now);
        return db.GetCollection<IntegrationCredential>("integrationCredentials").UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    /// <summary>An access token for one resource (Graph or Azure DevOps) obtained from the connection's refresh token, cached until shortly before it expires.</summary>
    internal static async Task<string> AccessTokenAsync(string userId, string resourceScope, MicrosoftAccountDoc account, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var cacheKey = account.ConnectionId + "|" + resourceScope;
        if (TokenCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return cached.Token;
        if (!TryGetSettings(config, out var settings)) throw new MicrosoftException(503, "Microsoft sign-in is not configured on the server.");

        var credential = await db.GetCollection<IntegrationCredential>("integrationCredentials").Find(c => c.ConnectionId == account.ConnectionId && c.CredentialKey == CredentialKey).FirstOrDefaultAsync(ct)
            ?? throw new MicrosoftException(401, "Microsoft is not connected. Connect your Microsoft account.", "not_connected");
        string refresh;
        try { refresh = protector.Unprotect(credential.EncryptedValue); }
        catch (CryptographicException) { throw new MicrosoftException(500, "Stored Microsoft credentials are invalid."); }

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", settings.ClientId), new("grant_type", "refresh_token"), new("refresh_token", refresh), new("scope", resourceScope + " offline_access"),
        };
        if (!string.IsNullOrEmpty(settings.ClientSecret)) form.Add(new("client_secret", settings.ClientSecret));
        using var response = await clients.CreateClient("ms-login").PostAsync($"{settings.Tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            // invalid_grant = the refresh token was revoked/expired; interaction_required/consent_required = the service was not consented yet.
            if (body.Contains("consent_required") || body.Contains("invalid_scope") || body.Contains("AADSTS65001"))
                throw new MicrosoftException(403, "That Microsoft service has not been allowed yet. Connect it from Apps.", "consent_required");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == account.ConnectionId,
                    Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Error).Set(c => c.LastError, "Microsoft authorization expired; reconnect required.").Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
                throw new MicrosoftException(401, "Microsoft authorization expired. Reconnect your Microsoft account.", "reconnect");
            }
            throw new MicrosoftException((int)response.StatusCode, "Microsoft could not refresh access. Please retry.");
        }
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrWhiteSpace(token?.AccessToken)) throw new MicrosoftException(502, "Microsoft returned an invalid token response.");
        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != refresh) await SaveTokenAsync(db, protector, account.ConnectionId, token.RefreshToken, ct);
        TokenCache[cacheKey] = (token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        return token.AccessToken;
    }

    internal sealed record Ctx(MicrosoftAccountDoc Account, IMongoDatabase Db, ISecretProtector Protector, IConfiguration Config, IHttpClientFactory Clients, string UserId);

    private static async Task<Ctx> RequireAsync(string feature, string userId, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct, bool write = false)
    {
        var account = await LoadAccountAsync(db, userId, ct) ?? throw new MicrosoftException(409, "Connect your Microsoft account first.", "not_connected");
        if (!account.Features.Contains(feature)) throw new MicrosoftException(403, "That Microsoft service is not enabled. Turn it on from Apps.", "consent_required");
        if (write && !account.Write) throw new MicrosoftException(403, "Daksha has read-only access to your Microsoft account. Turn on full access in Manage to do this.", "write_required");
        return new Ctx(account, db, protector, config, clients, userId);
    }

    /// <summary>Calls Microsoft Graph as the user. <paramref name="path"/> is relative to /v1.0/. The caller disposes the result.</summary>
    internal static async Task<JsonDocument> GraphAsync(string userId, MicrosoftAccountDoc account, string path, HttpMethod method, object? body, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct, string? prefer = null)
    {
        var token = await AccessTokenAsync(userId, MicrosoftFeatures.GraphResource, account, db, protector, config, clients, ct);
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prefer is not null) req.Headers.TryAddWithoutValidation("Prefer", prefer);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await clients.CreateClient("ms-graph").SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var message = "Microsoft Graph returned an error.";
            try { using var err = JsonDocument.Parse(text); if (err.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) message = m.GetString() ?? message; } catch (JsonException) { }
            throw new MicrosoftException((int)res.StatusCode, message);
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static IResult Fail(MicrosoftException ex) => Results.Problem(statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : 502, title: ex.Message, extensions: ex.Code is null ? null : new Dictionary<string, object?> { ["code"] = ex.Code });

    // ---- Outlook mail ------------------------------------------------------------------------------

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Date(JsonElement e, string k) => Str(e, k) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
    private static (string Name, string Address) Person(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var p) || !p.TryGetProperty("emailAddress", out var ea)) return ("", "");
        var address = Str(ea, "address") ?? "";
        return (Str(ea, "name") is { Length: > 0 } n ? n : address, address);
    }

    internal static OutlookMessageSummary ToSummary(JsonElement m)
    {
        var (name, address) = Person(m, "from");
        return new OutlookMessageSummary(Str(m, "id") ?? "", name, address, Str(m, "subject") ?? "(no subject)", Str(m, "bodyPreview") ?? "", Date(m, "receivedDateTime"), Bool(m, "isRead"), Bool(m, "hasAttachments"));
    }

    private static async Task<IResult> ListMailAsync(string? q, int? skip, string? folder, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        if (q is { Length: > 300 }) return Results.BadRequest("The search is too long.");
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.Mail, who.UserId, db, protector, config, clients, ct);
            var box = folder is "sentitems" or "drafts" or "deleteditems" or "archive" ? folder : "inbox";
            var p = new Dictionary<string, string?> { ["$top"] = "25", ["$select"] = "id,subject,from,receivedDateTime,bodyPreview,isRead,hasAttachments" };
            if (string.IsNullOrWhiteSpace(q)) { p["$orderby"] = "receivedDateTime desc"; if (skip is > 0) p["$skip"] = skip.Value.ToString(); }
            else p["$search"] = "\"" + q.Replace("\"", "") + "\"";
            using var doc = await GraphAsync(who.UserId, c.Account, QueryHelpers.AddQueryString($"me/mailFolders/{box}/messages", p), HttpMethod.Get, null, db, protector, config, clients, ct);
            var items = doc.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().Select(ToSummary).ToArray() : [];
            return Results.Ok(new OutlookMessagePage(items, items.Length == 25 && string.IsNullOrWhiteSpace(q) ? (skip ?? 0) + 25 : null));
        }
        catch (MicrosoftException ex) { return Fail(ex); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Microsoft could not be reached. Please retry."); }
    }

    private static async Task<IResult> GetMailAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.Mail, who.UserId, db, protector, config, clients, ct);
            using var doc = await GraphAsync(who.UserId, c.Account, $"me/messages/{Uri.EscapeDataString(id)}?$select=id,subject,from,toRecipients,receivedDateTime,body", HttpMethod.Get, null, db, protector, config, clients, ct);
            var m = doc.RootElement;
            var to = m.TryGetProperty("toRecipients", out var tr) ? string.Join(", ", tr.EnumerateArray().Select(r => r.TryGetProperty("emailAddress", out var ea) ? Str(ea, "address") : null).Where(a => !string.IsNullOrEmpty(a))) : "";
            var body = m.TryGetProperty("body", out var b) ? b : default;
            var isHtml = body.ValueKind == JsonValueKind.Object && string.Equals(Str(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new OutlookMessage(Str(m, "id") ?? id, Person(m, "from").Name, to, Str(m, "subject") ?? "(no subject)", Date(m, "receivedDateTime"), body.ValueKind == JsonValueKind.Object ? Str(body, "content") ?? "" : "", isHtml));
        }
        catch (MicrosoftException ex) { return Fail(ex); }
    }

    private static async Task<IResult> SendMailAsync(SendOutlookMessageRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.To) || !req.To.Contains('@') || req.To.Length > 320) return Results.Problem(statusCode: 400, title: "Enter a valid recipient address.");
        if (string.IsNullOrWhiteSpace(req.Subject) && string.IsNullOrWhiteSpace(req.Body)) return Results.Problem(statusCode: 400, title: "Write a subject or a message.");
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.Mail, who.UserId, db, protector, config, clients, ct, write: true);
            var settings = await LoadSettingsAsync(db, who.UserId, ct);
            var message = new
            {
                subject = (req.Subject ?? "").Trim(),
                body = new { contentType = "Text", content = (req.Body ?? "") + (string.IsNullOrWhiteSpace(settings.Signature) ? "" : "\n\n" + settings.Signature) },
                toRecipients = req.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).Select(a => new { emailAddress = new { address = a } }).ToArray(),
            };
            using var _ = await GraphAsync(who.UserId, c.Account, "me/sendMail", HttpMethod.Post, new { message, saveToSentItems = true }, db, protector, config, clients, ct);
            return Results.Accepted();
        }
        catch (MicrosoftException ex) { return Fail(ex); }
    }

    private static async Task<IResult> MarkReadAsync(string id, MarkOutlookReadRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.Mail, who.UserId, db, protector, config, clients, ct, write: true);
            using var _ = await GraphAsync(who.UserId, c.Account, $"me/messages/{Uri.EscapeDataString(id)}", HttpMethod.Patch, new { isRead = req.IsRead }, db, protector, config, clients, ct);
            return Results.NoContent();
        }
        catch (MicrosoftException ex) { return Fail(ex); }
    }

    // ---- OneDrive ----------------------------------------------------------------------------------

    private static async Task<IResult> ListFilesAsync(string? folderId, string? q, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        if (q is { Length: > 200 } || folderId is { Length: > 200 }) return Results.BadRequest("That request is too long.");
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.OneDrive, who.UserId, db, protector, config, clients, ct);
            const string select = "$select=id,name,size,folder,file,lastModifiedDateTime,webUrl&$top=100";
            var name = "OneDrive";
            string path;
            if (!string.IsNullOrWhiteSpace(q)) { path = $"me/drive/root/search(q='{Uri.EscapeDataString(q.Replace("'", "''"))}')?{select}"; name = $"Search: {q}"; }
            else if (!string.IsNullOrWhiteSpace(folderId))
            {
                path = $"me/drive/items/{Uri.EscapeDataString(folderId)}/children?{select}&$orderby=name";
                using var meta = await GraphAsync(who.UserId, c.Account, $"me/drive/items/{Uri.EscapeDataString(folderId)}?$select=name", HttpMethod.Get, null, db, protector, config, clients, ct);
                name = Str(meta.RootElement, "name") ?? "Folder";
            }
            else path = $"me/drive/root/children?{select}&$orderby=name";
            using var doc = await GraphAsync(who.UserId, c.Account, path, HttpMethod.Get, null, db, protector, config, clients, ct);
            var items = doc.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().Select(i =>
                new DriveItemDto(Str(i, "id") ?? "", Str(i, "name") ?? "", i.TryGetProperty("folder", out _), i.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0, Date(i, "lastModifiedDateTime"), Str(i, "webUrl"),
                    i.TryGetProperty("file", out var f) ? Str(f, "mimeType") : null))
                .OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray() : [];
            return Results.Ok(new DriveListing(folderId, name, items));
        }
        catch (MicrosoftException ex) { return Fail(ex); }
    }

    // ---- Azure DevOps ------------------------------------------------------------------------------

    private static async Task<JsonDocument> DevOpsAsync(Ctx c, string url, HttpMethod method, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (c.Account.DevOpsViaToken)
        {
            var credential = await c.Db.GetCollection<IntegrationCredential>("integrationCredentials").Find(x => x.ConnectionId == c.Account.ConnectionId && x.CredentialKey == DevOpsTokenKey).FirstOrDefaultAsync(ct)
                ?? throw new MicrosoftException(403, "The Azure DevOps token is missing. Add it again in Manage.", "consent_required");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth(c.Protector.Unprotect(credential.EncryptedValue)));
        }
        else
        {
            var token = await AccessTokenAsync(c.UserId, MicrosoftFeatures.DevOpsResource, c.Account, c.Db, c.Protector, c.Config, c.Clients, ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await c.Clients.CreateClient("ms-devops").SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new MicrosoftException(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 403 : (int)res.StatusCode,
                res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? (c.Account.DevOpsViaToken ? "Azure DevOps rejected the token. It may have expired: add a new one in Manage." : "Azure DevOps did not allow this account. Check the organization has Microsoft Entra access enabled. A personal Outlook account can use a token instead.")
                    : "Azure DevOps returned an error.");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static async Task<IResult> DevOpsSummaryAsync(string? org, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        if (org is { Length: > 100 } || (org is not null && !org.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))) return Results.BadRequest("That organization name is not valid.");
        try
        {
            var c = await RequireAsync(MicrosoftFeatures.DevOps, who.UserId, db, protector, config, clients, ct);
            var r = await DevOpsWorkItemsAsync(c, org, ct);
            return Results.Ok(new DevOpsSummary(r.Orgs, r.Org, r.Projects, r.Items));
        }
        catch (MicrosoftException ex) { return Fail(ex); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Azure DevOps could not be reached. Please retry."); }
    }

    // ---- dashboard ---------------------------------------------------------------------------------

    public sealed record MicrosoftDashboardDto(bool Connected, string? Email, IReadOnlyList<string> Features, int? Unread, IReadOnlyList<OutlookMessageSummary> Messages, IReadOnlyList<CalendarEventDto> Events, IReadOnlyList<DriveItemDto> Files, IReadOnlyList<DevOpsWorkItemDto> WorkItems, IReadOnlyList<string> Errors);

    /// <summary>One compact summary for the dashboard tile. Each service is read on its own, so one failing never hides the others.</summary>
    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, ct);
        if (account is null) return Results.Ok(new MicrosoftDashboardDto(false, null, [], null, [], [], [], [], []));
        var st = await LoadSettingsAsync(db, who.UserId, ct);
        var errors = new List<string>();
        int? unread = null; IReadOnlyList<OutlookMessageSummary> messages = []; IReadOnlyList<CalendarEventDto> events = []; IReadOnlyList<DriveItemDto> files = []; IReadOnlyList<DevOpsWorkItemDto> work = [];

        async Task Run(string feature, string label, Func<Task> body)
        {
            if (!account.Features.Contains(feature)) return;
            try { await body(); }
            catch (MicrosoftException ex) { errors.Add($"{label}: {ex.Message}"); }
            catch (HttpRequestException) { errors.Add($"{label}: Microsoft could not be reached."); }
        }
        await Task.WhenAll(
            Run(MicrosoftFeatures.Mail, "Outlook mail", async () =>
            {
                var filter = st.MailFilter switch
                {
                    "unread" => "&$filter=isRead eq false and receivedDateTime ge 1900-01-01T00:00:00Z",
                    "flagged" => "&$filter=flag/flagStatus eq 'flagged' and receivedDateTime ge 1900-01-01T00:00:00Z",
                    "important" => "&$filter=importance eq 'high' and receivedDateTime ge 1900-01-01T00:00:00Z",
                    _ => "",
                };
                using var doc = await GraphAsync(who.UserId, account, $"me/mailFolders/inbox/messages?$top={st.MailCount}&$select=id,subject,from,receivedDateTime,bodyPreview,isRead,hasAttachments&$orderby=receivedDateTime desc{filter}", HttpMethod.Get, null, db, protector, config, clients, ct);
                messages = doc.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().Select(ToSummary).ToArray() : [];
                using var inbox = await GraphAsync(who.UserId, account, "me/mailFolders/inbox?$select=unreadItemCount", HttpMethod.Get, null, db, protector, config, clients, ct);
                unread = inbox.RootElement.TryGetProperty("unreadItemCount", out var u) && u.TryGetInt32(out var n) ? n : null;
            }),
            Run(MicrosoftFeatures.Calendar, "Outlook calendar", async () =>
            {
                var tz = await CalendarAssistant.TimezoneAsync(db, who.UserId, ct);
                var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
                events = (await MicrosoftCalendar.ListAsync(db, protector, config, clients, who.UserId, today, today.AddDays(st.CalendarDays), ct))
                    .Where(e => st.CalendarShowAllDay || !e.AllDay).Take(st.CalendarCount).ToArray();
            }),
            Run(MicrosoftFeatures.OneDrive, "OneDrive", async () =>
            {
                using var doc = await GraphAsync(who.UserId, account, st.FileSource == "shared" ? $"me/drive/sharedWithMe?$top={st.FileCount}" : $"me/drive/recent?$top={st.FileCount}", HttpMethod.Get, null, db, protector, config, clients, ct);
                files = doc.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().Select(i =>
                    new DriveItemDto(Str(i, "id") ?? "", Str(i, "name") ?? "", i.TryGetProperty("folder", out _), i.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0, Date(i, "lastModifiedDateTime"), Str(i, "webUrl"), i.TryGetProperty("file", out var f) ? Str(f, "mimeType") : null)).ToArray() : [];
            }),
            Run(MicrosoftFeatures.DevOps, "Azure DevOps", async () =>
            {
                var c = new Ctx(account, db, protector, config, clients, who.UserId);
                work = (await DevOpsWorkItemsAsync(c, null, ct)).Items.Take(st.WorkItemCount).ToArray();
            }));
        return Results.Ok(new MicrosoftDashboardDto(true, account.Email, account.Features, unread, messages, events, files, work, errors));
    }

    /// <summary>The organizations, projects and open assigned work items of the user's Azure DevOps (first organization unless one is named).</summary>
    internal static async Task<(IReadOnlyList<string> Orgs, string? Org, IReadOnlyList<string> Projects, IReadOnlyList<DevOpsWorkItemDto> Items)> DevOpsWorkItemsAsync(Ctx c, string? org, CancellationToken ct)
    {
        var st = await LoadSettingsAsync(c.Db, c.UserId, ct);
        org ??= st.DevOpsDefaultOrg;
        string[] orgs;
        if (c.Account.DevOpsViaToken && !string.IsNullOrEmpty(c.Account.DevOpsOrg)) orgs = [c.Account.DevOpsOrg]; // a token belongs to one organization
        else
        {
            using var profile = await DevOpsAsync(c, "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1", HttpMethod.Get, null, ct);
            var memberId = Str(profile.RootElement, "id") ?? "";
            using var accounts = await DevOpsAsync(c, $"https://app.vssps.visualstudio.com/_apis/accounts?memberId={Uri.EscapeDataString(memberId)}&api-version=7.1", HttpMethod.Get, null, ct);
            orgs = accounts.RootElement.TryGetProperty("value", out var av) ? av.EnumerateArray().Select(a => Str(a, "accountName") ?? "").Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        var chosen = org is not null && orgs.Contains(org, StringComparer.OrdinalIgnoreCase) ? orgs.First(o => string.Equals(o, org, StringComparison.OrdinalIgnoreCase)) : orgs.FirstOrDefault();
        if (chosen is null) return (orgs, null, [], []);
        using var projects = await DevOpsAsync(c, $"https://dev.azure.com/{chosen}/_apis/projects?$top=100&api-version=7.1", HttpMethod.Get, null, ct);
        var projectNames = projects.RootElement.TryGetProperty("value", out var pv) ? pv.EnumerateArray().Select(p => Str(p, "name") ?? "").Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        var who = st.WorkItemScope == "created" ? "[System.CreatedBy]" : "[System.AssignedTo]";
        var open = st.WorkItemIncludeClosed ? "" : " AND [System.State] NOT IN ('Closed','Done','Removed','Resolved')";
        var wiql = new { query = $"SELECT [System.Id] FROM WorkItems WHERE {who} = @Me{open} ORDER BY [System.ChangedDate] DESC" };
        using var ids = await DevOpsAsync(c, $"https://dev.azure.com/{chosen}/_apis/wit/wiql?$top=25&api-version=7.1", HttpMethod.Post, wiql, ct);
        var idList = ids.RootElement.TryGetProperty("workItems", out var wv) ? wv.EnumerateArray().Select(w => w.TryGetProperty("id", out var i) ? i.GetInt32() : 0).Where(i => i > 0).Take(25).ToArray() : [];
        var items = new List<DevOpsWorkItemDto>();
        if (idList.Length > 0)
        {
            using var details = await DevOpsAsync(c, $"https://dev.azure.com/{chosen}/_apis/wit/workitems?ids={string.Join(',', idList)}&fields=System.Id,System.Title,System.State,System.WorkItemType,System.TeamProject&api-version=7.1", HttpMethod.Get, null, ct);
            if (details.RootElement.TryGetProperty("value", out var dv))
                foreach (var w in dv.EnumerateArray())
                {
                    var f = w.GetProperty("fields");
                    var project = Str(f, "System.TeamProject") ?? "";
                    var id = w.GetProperty("id").GetInt32();
                    items.Add(new DevOpsWorkItemDto(id, Str(f, "System.Title") ?? "", Str(f, "System.State") ?? "", Str(f, "System.WorkItemType") ?? "", project, $"https://dev.azure.com/{chosen}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}"));
                }
        }
        return (orgs, chosen, projectNames, items);
    }

    // ---- shared helpers ----------------------------------------------------------------------------

    private static IResult Back(string frontendUrl, string key, string value) => Results.Redirect(QueryHelpers.AddQueryString(frontendUrl, new Dictionary<string, string?> { [key] = value }));
    private static string HashState(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; } = 3600;
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }
}
