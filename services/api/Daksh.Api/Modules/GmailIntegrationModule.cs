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
public sealed record GmailMessageSummary(string Id, string ThreadId, string From, string Subject, string Snippet, DateTimeOffset? Date);
public sealed record GmailMessage(string Id, string From, string To, string Subject, DateTimeOffset Date, string Body);
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

public sealed class GmailIntegrationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class GmailIntegrationModule
{
    private const string IntegrationKey = "gmail";
    private const string CredentialKey = "gmail-oauth-token";
    private const string GmailScope = "https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddGmailIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("google-oauth", client => client.BaseAddress = new Uri("https://oauth2.googleapis.com/"));
        services.AddHttpClient("gmail-api", client => client.BaseAddress = new Uri("https://gmail.googleapis.com/gmail/v1/"));
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

    public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
        QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = GmailScope,
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

        return Results.Ok(new GmailAuthorizationResponse(BuildAuthorizationUrl(settings.ClientId, settings.RedirectUri, state, challenge)));
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
        var existing = await connections.Find(c => c.UserId == attempt.UserId && c.IntegrationKey == IntegrationKey)
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
        return RedirectToFrontend(attempt.FrontendUrl, "status", "connected");
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
                    return new GmailMessageSummary(
                        item.Id,
                        item.ThreadId,
                        Header(headers, "From"),
                        Header(headers, "Subject"),
                        snippet,
                        internalDate);
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
                mime.TextBody ?? mime.HtmlBody ?? ""));
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
            var context = await GetUserContextAsync(who.UserId, db, protector, ct);
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(context.Connection.AccountIdentifier!));
            message.To.AddRange(recipients);
            message.Subject = request.Subject.Trim();
            message.Body = new TextPart("plain") { Text = request.Body };
            var raw = Base64Url(Encoding.UTF8.GetBytes(message.ToString()));

            using var sent = await GmailApiAsync(who.UserId, "users/me/messages/send", HttpMethod.Post, new { raw }, db, protector, config, clients, ct);
            var id = sent.RootElement.TryGetProperty("id", out var sentId) ? sentId.GetString() : null;
            return Results.Ok(new { messageId = id });
        }
        catch (GmailIntegrationException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
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
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Daksh.Api.Gmail");
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var connection = await connections.Find(c => c.UserId == who.UserId && c.IntegrationKey == IntegrationKey)
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
        return Results.Ok(new { status = ConnectionStatus.Disconnected.ToString() });
    }

    private static async Task<JsonDocument> GmailApiAsync(
        string userId,
        string path,
        HttpMethod method,
        object? body,
        IMongoDatabase db,
        ISecretProtector protector,
        IConfiguration config,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var context = await GetUserContextAsync(userId, db, protector, ct);
        if (context.Tokens.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            context = context with { Tokens = await RefreshTokensAsync(context.Connection, context.Tokens, db, protector, config, clients, ct) };

        var client = clients.CreateClient("gmail-api");
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
            if (!response.IsSuccessStatusCode)
                throw new GmailIntegrationException((int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.Unauthorized
                        ? "Gmail authorization expired. Reconnect your Gmail account."
                        : "Gmail could not complete the request. Check the connection and permissions.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        throw new GmailIntegrationException(401, "Gmail authorization expired. Reconnect your Gmail account.");
    }

    private static async Task<GmailUserContext> GetUserContextAsync(
        string userId,
        IMongoDatabase db,
        ISecretProtector protector,
        CancellationToken ct)
    {
        var connection = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == userId && c.IntegrationKey == IntegrationKey && c.Status == ConnectionStatus.Connected)
            .SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
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
    }

    private sealed class GoogleProfile
    {
        [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
    }
}
