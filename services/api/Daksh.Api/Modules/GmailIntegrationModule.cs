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
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MimeKit;

namespace Daksh.Api.Modules;

public sealed record GmailAuthorizationResponse(string AuthorizationUrl);
public sealed record GmailMessageSummary(string Id, string ThreadId, string From, string Subject, string Snippet, DateTimeOffset? Date, bool IsUnread = false, bool IsStarred = false);
public sealed record GmailMessage(string Id, string From, string To, string Subject, DateTimeOffset Date, string Body, string? Html = null);
public sealed record GmailSettingsDto(string Signature, bool ShowImages, int DashboardCount, string DashboardQuery, bool ShowOnDashboard);
public sealed record UpdateGmailSettingsRequest(string? Signature, bool? ShowImages, int? DashboardCount, string? DashboardQuery);

[BsonIgnoreExtraElements]
public sealed class GmailUserSettings
{
    [BsonId] public required string UserId { get; set; }
    public string Signature { get; set; } = "";
    public bool ShowImages { get; set; }
    public int DashboardCount { get; set; } = 5;
    public string DashboardQuery { get; set; } = "in:inbox";
    public DateTime UpdatedAtUtc { get; set; }
}
public sealed record GmailMessagePage(IReadOnlyList<GmailMessageSummary> Messages, string? NextPageToken);
public sealed record SendGmailMessageRequest(string? To, string? Subject, string? Body);

public sealed class GmailOAuthAttempt
{
    [BsonId] public required string StateHash { get; set; }
    public required string UserId { get; set; }
    public required string ProtectedCodeVerifier { get; set; }
    public required string FrontendUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class GmailIntegrationException(int statusCode, string message, string? code = null, string? requiredScope = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    /// <summary>Machine-readable reason the UI can act on, e.g. <c>scope_upgrade_required</c> or <c>permission_denied</c>.</summary>
    public string? Code { get; } = code;
    public string? RequiredScope { get; } = requiredScope;
}

public static partial class GmailIntegrationModule
{
    private const string IntegrationKey = "gmail";
    private const string CredentialKey = "gmail-oauth-token";
    private const string GmailScope = "https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddGmailIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("google-oauth", client => client.BaseAddress = new Uri("https://oauth2.googleapis.com/"));
        services.AddHttpClient("gmail-api", client => client.BaseAddress = new Uri("https://gmail.googleapis.com/gmail/v1/"));
        services.AddSingleton<GmailServices>();
        services.AddSingleton<GmailPipeline>();
        services.AddSingleton<GmailSyncEngine>();
        services.AddHostedService<GmailSyncWorker>();
        return services;
    }

    public static void MapGmailIntegration(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations/gmail").WithTags("Gmail");

        group.MapPost("/connect", StartOAuthAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();
        group.MapGet("/messages", ListMessagesAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapGet("/messages/{messageId}", GetMessageAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapPost("/messages", SendMessageAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapPost("/disconnect", DisconnectAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapGet("/settings", GetSettingsAsync).RequireAuthorization(AuthSetup.UserScheme);
        group.MapPut("/settings", UpdateSettingsAsync).RequireAuthorization(AuthSetup.UserScheme);
        MapGmailManagement(group);
    }

    private static async Task<GmailUserSettings> LoadSettingsAsync(IMongoDatabase db, string userId, CancellationToken ct) =>
        await db.GetCollection<GmailUserSettings>("gmailSettings").Find(x => x.UserId == userId).FirstOrDefaultAsync(ct)
        ?? new GmailUserSettings { UserId = userId };

    private static async Task<GmailSettingsDto> ToSettingsDtoAsync(IMongoDatabase db, string userId, GmailUserSettings settings, CancellationToken ct)
    {
        var onDashboard = await db.GetCollection<UserDashboardApp>("userDashboardApps").Find(a => a.UserId == userId)
            .ToListAsync(ct);
        var connection = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == userId && c.IntegrationKey == "gmail" && c.Status == ConnectionStatus.Connected).FirstOrDefaultAsync(ct);
        var shown = connection is not null && onDashboard.Any(a => a.ConnectionId == connection.Id);
        return new GmailSettingsDto(settings.Signature, settings.ShowImages, settings.DashboardCount, settings.DashboardQuery, shown);
    }

    private static async Task<IResult> GetSettingsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        Results.Ok(await ToSettingsDtoAsync(db, who.UserId, await LoadSettingsAsync(db, who.UserId, ct), ct));

    private static async Task<IResult> UpdateSettingsAsync(UpdateGmailSettingsRequest request, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (request.Signature is { Length: > 2000 })
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["signature"] = ["The signature can be at most 2,000 characters."] });
        if (request.DashboardCount is { } count && (count < 3 || count > 10))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dashboardCount"] = ["Choose between 3 and 10 emails."] });
        if (request.DashboardQuery is { Length: > 200 })
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dashboardQuery"] = ["The dashboard filter is too long."] });

        var settings = await LoadSettingsAsync(db, who.UserId, ct);
        if (request.Signature is not null) settings.Signature = request.Signature.Trim();
        if (request.ShowImages is { } images) settings.ShowImages = images;
        if (request.DashboardCount is { } c) settings.DashboardCount = c;
        if (request.DashboardQuery is not null) settings.DashboardQuery = string.IsNullOrWhiteSpace(request.DashboardQuery) ? "in:inbox" : request.DashboardQuery.Trim();
        settings.UpdatedAtUtc = DateTime.UtcNow;
        await db.GetCollection<GmailUserSettings>("gmailSettings").ReplaceOneAsync(x => x.UserId == who.UserId, settings, new ReplaceOptions { IsUpsert = true }, ct);
        return Results.Ok(await ToSettingsDtoAsync(db, who.UserId, settings, ct));
    }

    public static async Task EnsureGmailIndexesAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        var attempts = database.GetCollection<GmailOAuthAttempt>("gmailOAuthAttempts");
        await attempts.Indexes.CreateOneAsync(
            new CreateIndexModel<GmailOAuthAttempt>(
                Builders<GmailOAuthAttempt>.IndexKeys.Ascending(a => a.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_gmailOAuthAttempts_expiresAtUtc" }),
            cancellationToken: ct);
    }

    public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge, string? scope = null) =>
        QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope ?? GmailScope,
            ["include_granted_scopes"] = "true",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    private static async Task<IResult> StartOAuthAsync(
        IMongoDatabase db,
        ClaimsPrincipalAccessor who,
        IConfiguration config,
        ISecretProtector protector,
        string? features,
        CancellationToken ct)
    {
        if (!TryGetSettings(config, out var settings))
            return Results.Problem(statusCode: 503, title: "Gmail OAuth is not configured.",
                detail: "Configure Gmail:OAuth:ClientId and Gmail:OAuth:ClientSecret in the API environment.");

        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations")
            .Find(c => c.IntegrationKey == IntegrationKey && c.IsEnabled).FirstOrDefaultAsync(ct);
        if (catalog is null) return Results.Problem(statusCode: 404, title: "Gmail is not available.");

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var now = DateTime.UtcNow;
        await db.GetCollection<GmailOAuthAttempt>("gmailOAuthAttempts").InsertOneAsync(new GmailOAuthAttempt
        {
            StateHash = HashState(state),
            UserId = who.UserId,
            ProtectedCodeVerifier = protector.Protect(verifier),
            FrontendUrl = settings.FrontendUrl,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
        }, cancellationToken: ct);

        return Results.Ok(new GmailAuthorizationResponse(BuildAuthorizationUrl(settings.ClientId, settings.RedirectUri, state, challenge, GmailScopes.ForFeatures(features))));
    }

    private static async Task<IResult> CompleteOAuthAsync(
        string? code,
        string? state,
        string? error,
        IMongoDatabase db,
        IConfiguration config,
        ISecretProtector protector,
        IHttpClientFactory clients,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        if (string.IsNullOrWhiteSpace(state)) return Results.BadRequest("OAuth state is missing.");

        var now = DateTime.UtcNow;
        var attempt = await db.GetCollection<GmailOAuthAttempt>("gmailOAuthAttempts")
            .FindOneAndDeleteAsync(a => a.StateHash == HashState(state) && a.ExpiresAtUtc > now, cancellationToken: ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error))
            return RedirectToFrontend(attempt.FrontendUrl, "error", "consent_denied");
        if (string.IsNullOrWhiteSpace(code))
            return RedirectToFrontend(attempt.FrontendUrl, "error", "missing_code");
        if (!TryGetSettings(config, out var settings))
            return RedirectToFrontend(attempt.FrontendUrl, "error", "oauth_not_configured");

        string verifier;
        try
        {
            verifier = protector.Unprotect(attempt.ProtectedCodeVerifier);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Could not read Gmail OAuth verifier for user {UserId}", attempt.UserId);
            return RedirectToFrontend(attempt.FrontendUrl, "error", "server_config");
        }

        GoogleTokenResponse? tokenResponse;
        try
        {
            using var response = await clients.CreateClient("google-oauth").PostAsync("token", new FormUrlEncodedContent(
            [
                new("client_id", settings.ClientId),
                new("client_secret", settings.ClientSecret),
                new("code", code),
                new("code_verifier", verifier),
                new("grant_type", "authorization_code"),
                new("redirect_uri", settings.RedirectUri),
            ]), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Google OAuth code exchange returned HTTP {StatusCode}", (int)response.StatusCode);
                return RedirectToFrontend(attempt.FrontendUrl, "error", "token_exchange");
            }
            tokenResponse = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Could not reach Google OAuth token endpoint");
            return RedirectToFrontend(attempt.FrontendUrl, "error", "google_unavailable");
        }

        if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken) || string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            return RedirectToFrontend(attempt.FrontendUrl, "error", "refresh_token_missing");

        GoogleProfile? profile;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "users/me/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResponse.AccessToken);
            using var response = await clients.CreateClient("gmail-api").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gmail profile lookup returned HTTP {StatusCode}", (int)response.StatusCode);
                return RedirectToFrontend(attempt.FrontendUrl, "error", "profile_lookup");
            }
            profile = await response.Content.ReadFromJsonAsync<GoogleProfile>(ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Could not reach the Gmail API during OAuth callback");
            return RedirectToFrontend(attempt.FrontendUrl, "error", "google_unavailable");
        }

        if (string.IsNullOrWhiteSpace(profile?.EmailAddress))
            return RedirectToFrontend(attempt.FrontendUrl, "error", "profile_lookup");

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var existing = await connections.Find(c => c.UserId == attempt.UserId && c.IntegrationKey == IntegrationKey && c.AccountIdentifier == profile.EmailAddress)
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        var connectedAt = DateTime.UtcNow;
        IntegrationConnection connection;
        if (existing is null)
        {
            connection = new IntegrationConnection
            {
                UserId = attempt.UserId,
                IntegrationKey = IntegrationKey,
                Status = ConnectionStatus.Connected,
                AccountIdentifier = profile.EmailAddress,
                ProviderAccountId = profile.EmailAddress,
                ConnectedAt = connectedAt,
                CreatedAt = connectedAt,
                UpdatedAt = connectedAt,
            };
            await connections.InsertOneAsync(connection, cancellationToken: ct);
        }
        else
        {
            connection = existing;
            await connections.UpdateOneAsync(c => c.Id == existing.Id && c.UserId == attempt.UserId,
                Builders<IntegrationConnection>.Update
                    .Set(c => c.Status, ConnectionStatus.Connected)
                    .Set(c => c.AccountIdentifier, profile.EmailAddress)
                    .Set(c => c.ProviderAccountId, profile.EmailAddress)
                    .Set(c => c.ConnectedAt, connectedAt)
                    .Set(c => c.DisconnectedAt, null)
                    .Set(c => c.LastError, null)
                    .Set(c => c.UpdatedAt, connectedAt), cancellationToken: ct);
        }

        var tokenSet = new GmailTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken, connectedAt.AddSeconds(tokenResponse.ExpiresIn));
        await SaveTokensAsync(db, protector, connection.Id, tokenSet, ct);
        await RegisterAccountAsync(db, connection, tokenResponse.Scope, ct);
        return RedirectToFrontend(attempt.FrontendUrl, "status", "connected");
    }

    /// <summary>A short list of recent mail (sender, subject, a snippet) for the assistant. Returns null when Gmail is not connected or cannot be read.</summary>
    internal static async Task<IReadOnlyList<GmailMessageSummary>?> RecentForAssistantAsync(string userId, string query, int max, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var path = QueryHelpers.AddQueryString("users/me/messages", new Dictionary<string, string?> { ["maxResults"] = Math.Clamp(max, 1, 15).ToString(), ["q"] = query });
            using var list = await GmailApiAsync(userId, path, HttpMethod.Get, null, db, protector, config, clients, ct);
            var ids = list.RootElement.TryGetProperty("messages", out var items) ? items.EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "").Where(i => i.Length > 0).ToArray() : [];
            var rows = await Task.WhenAll(ids.Select(async id =>
            {
                try
                {
                    using var detail = await GmailApiAsync(userId, $"users/me/messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date", HttpMethod.Get, null, db, protector, config, clients, ct);
                    var root = detail.RootElement;
                    var headers = root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var values) ? values.EnumerateArray().ToArray() : [];
                    var date = root.TryGetProperty("internalDate", out var d) && long.TryParse(d.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
                    var labels = root.TryGetProperty("labelIds", out var l) ? l.EnumerateArray().Select(x => x.GetString()).ToHashSet() : new HashSet<string?>();
                    return new GmailMessageSummary(id, "", Header(headers, "From"), Header(headers, "Subject"), root.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "", date, labels.Contains("UNREAD"), labels.Contains("STARRED"));
                }
                catch (Exception) { return null; }
            }));
            return rows.OfType<GmailMessageSummary>().ToArray();
        }
        catch (Exception) { return null; } // not connected, token problem, offline: the assistant simply has no mail to look at
    }

    private static async Task<IResult> ListMessagesAsync(
        string? q,
        string? pageToken,
        IMongoDatabase db,
        ClaimsPrincipalAccessor who,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        if (q is { Length: > 500 } || pageToken is { Length: > 2048 })
            return Results.BadRequest("The Gmail query or page token is too long.");

        var query = string.IsNullOrWhiteSpace(q) ? "in:inbox" : q.Trim();
        var parameters = new Dictionary<string, string?> { ["maxResults"] = "20", ["q"] = query };
        if (!string.IsNullOrWhiteSpace(pageToken)) parameters["pageToken"] = pageToken;
        var path = QueryHelpers.AddQueryString("users/me/messages", parameters);

        try
        {
            using var list = await GmailApiAsync(who.UserId, path, HttpMethod.Get, null, db, protector, config, clients, ct);
            var items = list.RootElement.TryGetProperty("messages", out var messageItems)
                ? messageItems.EnumerateArray().Select(m => new GoogleMessageListItem(
                    m.GetProperty("id").GetString() ?? "",
                    m.TryGetProperty("threadId", out var thread) ? thread.GetString() ?? "" : "",
                    m.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "")).ToArray()
                : Array.Empty<GoogleMessageListItem>();

            var summaries = await Task.WhenAll(items.Where(m => m.Id.Length > 0).Select(async item =>
            {
                try
                {
                    using var detail = await GmailApiAsync(who.UserId,
                        $"users/me/messages/{Uri.EscapeDataString(item.Id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date",
                        HttpMethod.Get, null, db, protector, config, clients, ct);
                    var root = detail.RootElement;
                    var headers = root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var values)
                        ? values.EnumerateArray().ToArray()
                        : Array.Empty<JsonElement>();
                    var internalDate = root.TryGetProperty("internalDate", out var dateValue) &&
                                       long.TryParse(dateValue.GetString(), out var milliseconds)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                        : (DateTimeOffset?)null;
                    var snippet = root.TryGetProperty("snippet", out var messageSnippet)
                        ? messageSnippet.GetString() ?? item.Snippet
                        : item.Snippet;
                    var labels = root.TryGetProperty("labelIds", out var labelValues)
                        ? labelValues.EnumerateArray().Select(l => l.GetString()).ToHashSet()
                        : new HashSet<string?>();
                    return new GmailMessageSummary(
                        item.Id,
                        item.ThreadId,
                        Header(headers, "From"),
                        Header(headers, "Subject"),
                        snippet,
                        internalDate,
                        labels.Contains("UNREAD"),
                        labels.Contains("STARRED"));
                }
                catch (GmailIntegrationException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
                {
                    return null;
                }
            }));

            var nextPageToken = list.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            return Results.Ok(new GmailMessagePage(summaries.OfType<GmailMessageSummary>().ToArray(), nextPageToken));
        }
        catch (GmailIntegrationException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Gmail message search failed for user {UserId}", who.UserId);
            return Results.Problem(statusCode: 502, title: "Could not reach Gmail. Please try again.");
        }
    }

    private static async Task<IResult> GetMessageAsync(
        string messageId,
        IMongoDatabase db,
        ClaimsPrincipalAccessor who,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        try
        {
            using var document = await GmailApiAsync(who.UserId,
                $"users/me/messages/{Uri.EscapeDataString(messageId)}?format=raw",
                HttpMethod.Get, null, db, protector, config, clients, ct);
            var raw = document.RootElement.GetProperty("raw").GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return Results.Problem(statusCode: 502, title: "Gmail returned an empty message.");

            using var stream = new MemoryStream(FromBase64Url(raw));
            var mime = MimeMessage.Load(stream);
            return Results.Ok(new GmailMessage(
                messageId,
                mime.From.ToString(),
                mime.To.ToString(),
                mime.Subject ?? "",
                mime.Date,
                mime.TextBody ?? mime.HtmlBody ?? "",
                mime.HtmlBody));
        }
        catch (GmailIntegrationException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Gmail message read failed for user {UserId}", who.UserId);
            return Results.Problem(statusCode: 502, title: "Could not reach Gmail. Please try again.");
        }
        catch (FormatException)
        {
            return Results.Problem(statusCode: 502, title: "Gmail returned a message that could not be decoded.");
        }
    }

    private static async Task<IResult> SendMessageAsync(
        SendGmailMessageRequest request,
        IMongoDatabase db,
        ClaimsPrincipalAccessor who,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        if (string.IsNullOrWhiteSpace(request.To) || request.To.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Enter one or more valid recipients."] });
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 998 || request.Subject.Contains('\r') || request.Subject.Contains('\n'))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["subject"] = ["Enter a subject without line breaks."] });
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 100_000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Enter a message up to 100,000 characters."] });

        InternetAddressList recipients;
        try
        {
            recipients = InternetAddressList.Parse(request.To);
        }
        catch (ParseException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Enter valid email addresses."] });
        }
        if (recipients.Count == 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Enter one or more valid recipients."] });

        try
        {
            var authorized = await GmailGuard.AuthorizeAsync(db, who.UserId, null, GmailPermission.Send, ct);
            var context = await GetUserContextAsync(who.UserId, authorized.Connection.Id, db, protector, ct);
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(context.Connection.AccountIdentifier!));
            message.To.AddRange(recipients);
            message.Subject = request.Subject.Trim();
            var signature = (await LoadSettingsAsync(db, who.UserId, ct)).Signature;
            var text = string.IsNullOrWhiteSpace(signature) ? request.Body : $"{request.Body}\r\n\r\n-- \r\n{signature}";
            message.Body = new TextPart("plain") { Text = text };
            var raw = Base64Url(Encoding.UTF8.GetBytes(message.ToString()));

            using var sent = await GmailApiForAsync(who.UserId, context.Connection.Id, "users/me/messages/send", HttpMethod.Post, new { raw }, db, protector, config, clients, ct);
            var id = sent.RootElement.TryGetProperty("id", out var sentId) ? sentId.GetString() : null;
            await db.GetCollection<GmailActionAudit>("gmailActionAudit").InsertOneAsync(new GmailActionAudit { UserId = who.UserId, AccountId = authorized.Connection.Id, MessageRef = id, Action = "message.send", AtUtc = DateTime.UtcNow }, cancellationToken: ct);
            return Results.Ok(new { messageId = id });
        }
        catch (GmailIntegrationException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode == 401 ? 409 : ex.StatusCode, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["requiredScope"] = ex.RequiredScope });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Gmail send failed for user {UserId}", who.UserId);
            return Results.Problem(statusCode: 502, title: "Could not send the message through Gmail.");
        }
    }

    private static async Task<IResult> DisconnectAsync(
        IMongoDatabase db,
        ClaimsPrincipalAccessor who,
        ISecretProtector protector,
        IHttpClientFactory clients,
        ILoggerFactory loggerFactory,
        string? accountId,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await connections.Find(c => c.UserId == who.UserId && c.IntegrationKey == IntegrationKey
                && c.Status == ConnectionStatus.Connected && (accountId == null || c.Id == accountId))
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (connection is null) return Results.NotFound();

        var credentials = db.GetCollection<IntegrationCredential>("integrationCredentials");
        var credential = await credentials.Find(c => c.ConnectionId == connection.Id && c.CredentialKey == CredentialKey).FirstOrDefaultAsync(ct);
        if (credential is not null)
        {
            GmailTokens? tokens;
            try
            {
                tokens = JsonSerializer.Deserialize<GmailTokens>(protector.Unprotect(credential.EncryptedValue), JsonOptions);
            }
            catch (CryptographicException)
            {
                return Results.Problem(statusCode: 500, title: "Could not read Gmail credentials. The connection was not changed.");
            }
            catch (JsonException)
            {
                return Results.Problem(statusCode: 500, title: "Stored Gmail credentials are invalid. The connection was not changed.");
            }

            if (!string.IsNullOrWhiteSpace(tokens?.RefreshToken))
            {
                try
                {
                    using var response = await clients.CreateClient("google-oauth").PostAsync("revoke",
                        new FormUrlEncodedContent([new("token", tokens.RefreshToken)]), ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Google token revocation returned HTTP {StatusCode}", (int)response.StatusCode);
                        return Results.Problem(statusCode: 502, title: "Google could not revoke access. The connection was not changed.");
                    }
                }
                catch (HttpRequestException ex)
                {
                    logger.LogError(ex, "Could not reach Google to revoke Gmail access");
                    return Results.Problem(statusCode: 502, title: "Could not reach Google to revoke access. The connection was not changed.");
                }
            }
        }

        await credentials.DeleteOneAsync(c => c.ConnectionId == connection.Id && c.CredentialKey == CredentialKey, ct);
        await connections.UpdateOneAsync(c => c.Id == connection.Id && c.UserId == who.UserId,
            Builders<IntegrationConnection>.Update
                .Set(c => c.Status, ConnectionStatus.Disconnected)
                .Set(c => c.DisconnectedAt, DateTime.UtcNow)
                .Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
        await GmailLocalData.PurgeAccountAsync(db, who.UserId, connection.Id, keepAudit: true, ct);
        return Results.Ok(new { status = ConnectionStatus.Disconnected.ToString() });
    }

    private static Task<JsonDocument> GmailApiAsync(
        string userId, string path, HttpMethod method, object? body,
        IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        GmailApiForAsync(userId, null, path, method, body, db, protector, config, clients, ct);

    /// <summary>Calls the Gmail API as <paramref name="userId"/>'s account <paramref name="connectionId"/> (default account when null).
    /// Refreshes the access token, retries 429/5xx with exponential backoff + jitter, and never throws Google's raw error body.</summary>
    internal static async Task<JsonDocument> GmailApiForAsync(
        string userId,
        string? connectionId,
        string path,
        HttpMethod method,
        object? body,
        IMongoDatabase db,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var context = await GetUserContextAsync(userId, connectionId, db, protector, ct);
        if (context.Tokens.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            context = context with { Tokens = await RefreshTokensAsync(context.Connection, context.Tokens, db, protector, config, clients, ct) };

        var client = clients.CreateClient("gmail-api");
        var transientRetries = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Tokens.AccessToken);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                context = context with { Tokens = await RefreshTokensAsync(context.Connection, context.Tokens, db, protector, config, clients, ct) };
                continue;
            }
            // Google reports its per-minute quota as 403 "rateLimitExceeded", which is a wait-and-retry condition, not a permission problem.
            var errorBody = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync(ct);
            var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && (errorBody.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || errorBody.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)));
            if ((rateLimited || response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError)
                && transientRetries < 3)
            {
                transientRetries++;
                attempt--; // transient retries do not consume the single token-refresh retry
                await Task.Delay(rateLimited ? TimeSpan.FromSeconds(8 * transientRetries) : GmailBackoff.Delay(transientRetries), ct); // the quota is per minute, so wait longer than for a blip
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new GmailIntegrationException(rateLimited ? 429 : (int)response.StatusCode, response.StatusCode switch
                {
                    _ when rateLimited => "Gmail is rate limiting requests. It will retry shortly.",
                    HttpStatusCode.Unauthorized => "Gmail authorization expired. Reconnect your Gmail account.",
                    HttpStatusCode.Forbidden => "Google denied this request. The account may need additional permission — reconnect Gmail and approve it.",
                    HttpStatusCode.NotFound => "Gmail could not find that item.",
                    _ => "Gmail could not complete the request. Check the connection and permissions.",
                }, rateLimited ? "rate_limited" : response.StatusCode == HttpStatusCode.Forbidden ? "google_forbidden" : null);

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(bytes);
        }
        throw new GmailIntegrationException(401, "Gmail authorization expired. Reconnect your Gmail account.");
    }

    private static Task<GmailUserContext> GetUserContextAsync(string userId, IMongoDatabase db, ISecretProtector protector, CancellationToken ct) =>
        GetUserContextAsync(userId, null, db, protector, ct);

    private static async Task<GmailUserContext> GetUserContextAsync(
        string userId,
        string? connectionId,
        IMongoDatabase db,
        ISecretProtector protector,
        CancellationToken ct)
    {
        var connection = await ResolveConnectionAsync(db, userId, connectionId, ct);
        if (connection is null)
            throw new GmailIntegrationException(409, "Connect a Gmail account to continue.");

        var credential = await db.GetCollection<IntegrationCredential>("integrationCredentials")
            .Find(c => c.ConnectionId == connection.Id && c.CredentialKey == CredentialKey).FirstOrDefaultAsync(ct);
        if (credential is null)
            throw new GmailIntegrationException(409, "Gmail credentials are missing. Reconnect your account.");

        try
        {
            var tokens = JsonSerializer.Deserialize<GmailTokens>(protector.Unprotect(credential.EncryptedValue), JsonOptions);
            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
                throw new JsonException("Required Gmail token fields are missing.");
            return new GmailUserContext(connection, tokens);
        }
        catch (CryptographicException)
        {
            throw new GmailIntegrationException(500, "Stored Gmail credentials could not be decrypted.");
        }
        catch (JsonException)
        {
            throw new GmailIntegrationException(500, "Stored Gmail credentials are invalid.");
        }
    }

    private static async Task<GmailTokens> RefreshTokensAsync(
        IntegrationConnection connection,
        GmailTokens tokens,
        IMongoDatabase db,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!TryGetSettings(config, out var settings))
            throw new GmailIntegrationException(503, "Gmail OAuth is not configured on the server.");

        using var response = await clients.CreateClient("google-oauth").PostAsync("token", new FormUrlEncodedContent(
        [
            new("client_id", settings.ClientId),
            new("client_secret", settings.ClientSecret),
            new("refresh_token", tokens.RefreshToken),
            new("grant_type", "refresh_token"),
        ]), ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(
                    c => c.Id == connection.Id,
                    Builders<IntegrationConnection>.Update
                        .Set(c => c.Status, ConnectionStatus.Error)
                        .Set(c => c.LastError, "Gmail authorization expired; reconnect required.")
                        .Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
                throw new GmailIntegrationException(401, "Gmail authorization expired. Reconnect your Gmail account.");
            }
            throw new GmailIntegrationException((int)response.StatusCode, "Google could not refresh Gmail access. Please retry.");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(ct);
        if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken))
            throw new GmailIntegrationException(502, "Google returned an invalid token response.");

        var refreshed = new GmailTokens(
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken ?? tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn));
        await SaveTokensAsync(db, protector, connection.Id, refreshed, ct);
        return refreshed;
    }

    private static Task SaveTokensAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, GmailTokens tokens, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<IntegrationCredential>.Filter.Eq(c => c.ConnectionId, connectionId) &
                     Builders<IntegrationCredential>.Filter.Eq(c => c.CredentialKey, CredentialKey);
        var update = Builders<IntegrationCredential>.Update
            .Set(c => c.EncryptedValue, protector.Protect(JsonSerializer.Serialize(tokens, JsonOptions)))
            .Set(c => c.EncryptionVersion, "v1")
            .SetOnInsert(c => c.ConnectionId, connectionId)
            .SetOnInsert(c => c.CredentialKey, CredentialKey)
            .SetOnInsert(c => c.CreatedAt, now)
            .Set(c => c.UpdatedAt, now);
        return db.GetCollection<IntegrationCredential>("integrationCredentials")
            .UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    private static bool TryGetSettings(IConfiguration config, out GmailOAuthSettings settings)
    {
        var clientId = config["Gmail:OAuth:ClientId"];
        var clientSecret = config["Gmail:OAuth:ClientSecret"];
        var redirectUri = config["Gmail:OAuth:RedirectUri"] ?? "http://localhost:5080/api/integrations/gmail/callback";
        var frontendUrl = config["Gmail:OAuth:FrontendUrl"] ?? "http://localhost:3000/apps/gmail";
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
            !TryGetAllowedUri(redirectUri, out _) || !TryGetAllowedUri(frontendUrl, out _))
        {
            settings = null!;
            return false;
        }
        settings = new GmailOAuthSettings(clientId, clientSecret, redirectUri, frontendUrl);
        return true;
    }

    private static bool TryGetAllowedUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return true;
        uri = null!;
        return false;
    }

    private static IResult RedirectToFrontend(string frontendUrl, string key, string value) =>
        Results.Redirect(QueryHelpers.AddQueryString(frontendUrl, new Dictionary<string, string?> { [key] = value }));

    private static string Header(JsonElement[] headers, string name)
    {
        foreach (var header in headers)
        {
            if (header.TryGetProperty("name", out var headerName) &&
                string.Equals(headerName.GetString(), name, StringComparison.OrdinalIgnoreCase) &&
                header.TryGetProperty("value", out var value))
                return value.GetString() ?? "";
        }
        return "";
    }

    private static string HashState(string state) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='));
    }

    private sealed record GmailOAuthSettings(string ClientId, string ClientSecret, string RedirectUri, string FrontendUrl);
    private sealed record GmailTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc);
    private sealed record GmailUserContext(IntegrationConnection Connection, GmailTokens Tokens);
    private sealed record GoogleMessageListItem(string Id, string ThreadId, string Snippet);

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; } = 3600;
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    private sealed class GoogleProfile
    {
        [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
    }
}
