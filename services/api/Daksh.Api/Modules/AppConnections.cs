using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// Shared plumbing for the "one account per person" apps (GitHub, Notion, Discord, Spotify): the sign-in attempt, the connection and
// encrypted credential, the account record, per-person settings, and the common /status, /write and /settings endpoints.
// Each app adds only what is specific to it (its OAuth exchange and its own features).

/// <summary>One setting an app offers. <see cref="Kind"/> is int (Min..Max), bool, enum (Options) or text (Max = longest allowed).</summary>
public sealed record SettingSpec(string Key, string Kind, string Default, int Min = 0, int Max = 0, string[]? Options = null);

/// <summary>What the common endpoints need to know about an app.</summary>
public sealed record AppInfo(string Key, string Name, SettingSpec[] Settings, Func<IConfiguration, bool> IsConfigured, bool SoftWrite = false);

public sealed record AppStatusDto(bool Configured, bool Connected, string? ConnectionId, string? AccountId, string? AccountName, bool Write, string? Scopes, IReadOnlyDictionary<string, string> Extra, string? Error);
public sealed record AppAuthorizationResponse(string AuthorizationUrl);
public sealed record SetAppWriteRequest(bool Enabled);
public sealed record DashItem(string Id, string Title, string? Subtitle, string? Url, string? Badge);
public sealed record DashSection(string Title, string? Href, IReadOnlyList<DashItem> Items);
public sealed record AppDashboardDto(bool Connected, string? Account, IReadOnlyList<DashSection> Sections, IReadOnlyList<string> Errors);

[BsonIgnoreExtraElements]
public sealed class AppAccountDoc
{
    [BsonId] public required string ConnectionId { get; set; }
    public required string UserId { get; set; }
    public required string AppKey { get; set; }
    public required string AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public bool Write { get; set; }
    public string Scopes { get; set; } = "";
    public Dictionary<string, string> Extra { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class AppUserSettings
{
    /// <summary>appKey:userId</summary>
    [BsonId] public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string AppKey { get; set; }
    public Dictionary<string, string> Values { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AppOAuthAttempt
{
    [BsonId] public required string StateHash { get; set; }
    public required string AppKey { get; set; }
    public required string UserId { get; set; }
    public required string FrontendUrl { get; set; }
    public bool Write { get; set; }
    /// <summary>App-specific choices made before signing in (for example GitHub's "private" repositories).</summary>
    public string Options { get; set; } = "";
    public string? ProtectedCodeVerifier { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class AppException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

public sealed record OAuthConfig(string ClientId, string? ClientSecret, string RedirectUri, string FrontendUrl);

public static class AppConnections
{
    // ---- setup ---------------------------------------------------------------------------------

    public static async Task EnsureIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        await db.GetCollection<AppOAuthAttempt>("appOAuthAttempts").Indexes.CreateOneAsync(
            new CreateIndexModel<AppOAuthAttempt>(Builders<AppOAuthAttempt>.IndexKeys.Ascending(a => a.ExpiresAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_appOAuthAttempts_expiresAtUtc" }), cancellationToken: ct);
        await db.GetCollection<AppAccountDoc>("appAccounts").Indexes.CreateOneAsync(
            new CreateIndexModel<AppAccountDoc>(Builders<AppAccountDoc>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.AppKey), new CreateIndexOptions { Name = "ix_appAccounts_user_app" }), cancellationToken: ct);
    }

    public static bool IsAllowedUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>Reads "{section}:OAuth:ClientId/ClientSecret/RedirectUri/FrontendUrl". A secret is required unless <paramref name="secretOptional"/>.</summary>
    public static bool TryGetConfig(IConfiguration config, string section, string appPath, bool secretOptional, out OAuthConfig result)
    {
        var id = config[$"{section}:OAuth:ClientId"];
        var secret = config[$"{section}:OAuth:ClientSecret"];
        var redirect = config[$"{section}:OAuth:RedirectUri"] ?? $"http://localhost:5080/api/integrations/{appPath}/callback";
        var frontend = config[$"{section}:OAuth:FrontendUrl"] ?? $"http://localhost:3000/apps/{appPath}";
        if (string.IsNullOrWhiteSpace(id) || (!secretOptional && string.IsNullOrWhiteSpace(secret)) || !IsAllowedUri(redirect) || !IsAllowedUri(frontend)) { result = null!; return false; }
        result = new OAuthConfig(id, string.IsNullOrWhiteSpace(secret) ? null : secret, redirect, frontend);
        return true;
    }

    // ---- sign-in attempts ----------------------------------------------------------------------

    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string HashState(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    public static async Task<string> StartAttemptAsync(IMongoDatabase db, string appKey, string userId, string frontendUrl, bool write, string options, string? protectedVerifier, CancellationToken ct)
    {
        var state = NewState();
        var now = DateTime.UtcNow;
        await db.GetCollection<AppOAuthAttempt>("appOAuthAttempts").InsertOneAsync(new AppOAuthAttempt
        {
            StateHash = HashState(state), AppKey = appKey, UserId = userId, FrontendUrl = frontendUrl, Write = write, Options = options,
            ProtectedCodeVerifier = protectedVerifier, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(10),
        }, cancellationToken: ct);
        return state;
    }

    /// <summary>Finds and consumes the attempt for this state (one use only, ten minutes).</summary>
    public static Task<AppOAuthAttempt?> TakeAttemptAsync(IMongoDatabase db, string appKey, string? state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state)) return Task.FromResult<AppOAuthAttempt?>(null);
        var hash = HashState(state);
        var now = DateTime.UtcNow;
        return db.GetCollection<AppOAuthAttempt>("appOAuthAttempts").FindOneAndDeleteAsync(a => a.StateHash == hash && a.AppKey == appKey && a.ExpiresAtUtc > now, cancellationToken: ct)!;
    }

    /// <summary>Sends the browser back to the app page with <c>?connected=1</c> or <c>?error=reason</c> (the sign-in popup reads this and closes).</summary>
    public static IResult Back(string frontendUrl, string appKey, string result) =>
        Results.Redirect(QueryHelpers.AddQueryString(frontendUrl, new Dictionary<string, string?> { ["app"] = appKey, [result == "connected" ? "connected" : "error"] = result == "connected" ? "1" : result }));

    // ---- connections, credentials and accounts -------------------------------------------------

    /// <summary>Creates (or reconnects) the user's connection for this app and account, and records the account.</summary>
    public static async Task<IntegrationConnection> SaveAccountAsync(IMongoDatabase db, string userId, string appKey, string accountId, string accountName, bool write, string scopes, Dictionary<string, string>? extra, CancellationToken ct)
    {
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var at = DateTime.UtcNow;
        // One account per person per app: reconnecting (even as someone else) replaces it.
        var existing = (await connections.Find(c => c.UserId == userId && c.IntegrationKey == appKey).SortByDescending(c => c.CreatedAt).ToListAsync(ct)).FirstOrDefault();
        IntegrationConnection connection;
        if (existing is null)
        {
            connection = new IntegrationConnection
            {
                UserId = userId, IntegrationKey = appKey, Status = ConnectionStatus.Connected, DisplayName = accountName,
                AccountIdentifier = accountName, ProviderAccountId = accountId, ConnectedAt = at, CreatedAt = at, UpdatedAt = at,
            };
            await connections.InsertOneAsync(connection, cancellationToken: ct);
        }
        else
        {
            connection = existing;
            await connections.UpdateOneAsync(c => c.Id == existing.Id, Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connected)
                .Set(c => c.DisplayName, accountName).Set(c => c.AccountIdentifier, accountName).Set(c => c.ProviderAccountId, accountId).Set(c => c.ConnectedAt, at)
                .Set(c => c.DisconnectedAt, null).Set(c => c.LastError, null).Set(c => c.UpdatedAt, at), cancellationToken: ct);
        }
        await db.GetCollection<AppAccountDoc>("appAccounts").ReplaceOneAsync(a => a.ConnectionId == connection.Id, new AppAccountDoc
        {
            ConnectionId = connection.Id, UserId = userId, AppKey = appKey, AccountId = accountId, AccountName = accountName, Write = write, Scopes = scopes,
            Extra = extra ?? [], UpdatedAtUtc = at,
        }, new ReplaceOptions { IsUpsert = true }, ct);
        return connection;
    }

    public static async Task SaveSecretAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, string credentialKey, string value, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationCredential>("integrationCredentials").UpdateOneAsync(
            Builders<IntegrationCredential>.Filter.Eq(c => c.ConnectionId, connectionId) & Builders<IntegrationCredential>.Filter.Eq(c => c.CredentialKey, credentialKey),
            Builders<IntegrationCredential>.Update.Set(c => c.EncryptedValue, protector.Protect(value)).Set(c => c.EncryptionVersion, "v1")
                .SetOnInsert(c => c.ConnectionId, connectionId).SetOnInsert(c => c.CredentialKey, credentialKey).SetOnInsert(c => c.CreatedAt, now).Set(c => c.UpdatedAt, now),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    public static async Task<string?> LoadSecretAsync(IMongoDatabase db, ISecretProtector protector, string connectionId, string credentialKey, CancellationToken ct)
    {
        var credential = await db.GetCollection<IntegrationCredential>("integrationCredentials").Find(c => c.ConnectionId == connectionId && c.CredentialKey == credentialKey).FirstOrDefaultAsync(ct);
        return credential is null ? null : protector.Unprotect(credential.EncryptedValue);
    }

    /// <summary>The user's live account for an app (connected, newest first), or null.</summary>
    public static async Task<AppAccountDoc?> LoadAccountAsync(IMongoDatabase db, string userId, string appKey, CancellationToken ct)
    {
        var accounts = await db.GetCollection<AppAccountDoc>("appAccounts").Find(a => a.UserId == userId && a.AppKey == appKey).SortByDescending(a => a.UpdatedAtUtc).ToListAsync(ct);
        foreach (var a in accounts)
            if (await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == a.ConnectionId && c.Status == ConnectionStatus.Connected).AnyAsync(ct)) return a;
        return null;
    }

    public static async Task<bool> IsConnectedAsync(IMongoDatabase db, string userId, string appKey, CancellationToken ct) => await LoadAccountAsync(db, userId, appKey, ct) is not null;

    /// <summary>Marks the connection disconnected, deletes its secrets, account, settings-independent data and dashboard tile.</summary>
    public static async Task RemoveAsync(IMongoDatabase db, AppAccountDoc account, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == account.ConnectionId && c.UserId == account.UserId,
            Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Disconnected).Set(c => c.DisconnectedAt, now).Set(c => c.UpdatedAt, now), cancellationToken: ct);
        await db.GetCollection<IntegrationCredential>("integrationCredentials").DeleteManyAsync(c => c.ConnectionId == account.ConnectionId, ct);
        await db.GetCollection<AppAccountDoc>("appAccounts").DeleteOneAsync(a => a.ConnectionId == account.ConnectionId, ct);
        await db.GetCollection<UserDashboardApp>("userDashboardApps").DeleteManyAsync(a => a.UserId == account.UserId && a.ConnectionId == account.ConnectionId, ct);
    }

    public static async Task MarkReconnectAsync(IMongoDatabase db, string connectionId, string message, CancellationToken ct) =>
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == connectionId,
            Builders<IntegrationConnection>.Update.Set(c => c.LastError, message).Set(c => c.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);

    // ---- settings ------------------------------------------------------------------------------

    public static async Task<Dictionary<string, string>> LoadSettingsAsync(IMongoDatabase db, AppInfo app, string userId, CancellationToken ct)
    {
        var stored = await db.GetCollection<AppUserSettings>("appSettings").Find(s => s.Id == $"{app.Key}:{userId}").FirstOrDefaultAsync(ct);
        var values = app.Settings.ToDictionary(s => s.Key, s => s.Default);
        if (stored is not null) foreach (var (k, v) in stored.Values) if (values.ContainsKey(k)) values[k] = v;
        return values;
    }

    public static int Int(Dictionary<string, string> values, string key, int fallback) => values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
    public static bool Bool(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var v) && v == "true";
    public static string Text(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : "";

    private static object? Typed(SettingSpec spec, string value) => spec.Kind switch
    {
        "int" => int.TryParse(value, out var n) ? n : int.TryParse(spec.Default, out var d) ? d : 0,
        "bool" => value == "true",
        _ => value,
    };

    /// <summary>Checks a requested change against the app's specs. Returns an error message, or null when it is fine.</summary>
    public static string? Validate(SettingSpec spec, JsonElement value)
    {
        switch (spec.Kind)
        {
            case "int": return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= spec.Min && n <= spec.Max ? null : $"Choose between {spec.Min} and {spec.Max}.";
            case "bool": return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Choose on or off.";
            case "enum": return value.ValueKind == JsonValueKind.String && spec.Options!.Contains(value.GetString()) ? null : "Unknown option.";
            default: return value.ValueKind == JsonValueKind.String && (value.GetString()?.Length ?? 0) <= spec.Max ? null : $"Use at most {spec.Max} characters.";
        }
    }

    // ---- common endpoints ----------------------------------------------------------------------

    /// <summary>Adds GET /status, POST /write (turning full access off; on, for apps where it needs no new permission), GET and PUT /settings.</summary>
    public static void MapCommon(this RouteGroupBuilder group, AppInfo app)
    {
        group.MapGet("/status", async (IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct) =>
        {
            var configured = app.IsConfigured(config);
            var a = await LoadAccountAsync(db, who.UserId, app.Key, ct);
            return Results.Ok(a is null
                ? new AppStatusDto(configured, false, null, null, null, false, null, new Dictionary<string, string>(), null)
                : new AppStatusDto(configured, true, a.ConnectionId, a.AccountId, a.AccountName, a.Write, a.Scopes, a.Extra, null));
        });

        group.MapPost("/write", async (SetAppWriteRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            if (r.Enabled && !app.SoftWrite) return Results.Problem(statusCode: 400, title: "Use Connect with full access to turn this on.");
            var a = await LoadAccountAsync(db, who.UserId, app.Key, ct);
            if (a is null) return Results.Problem(statusCode: 409, title: $"{app.Name} is not connected.");
            await db.GetCollection<AppAccountDoc>("appAccounts").UpdateOneAsync(x => x.ConnectionId == a.ConnectionId, Builders<AppAccountDoc>.Update.Set(x => x.Write, r.Enabled).Set(x => x.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
            return Results.NoContent();
        });

        group.MapGet("/settings", async (IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) => Results.Ok(await ToDtoAsync(db, app, who.UserId, ct)));

        group.MapPut("/settings", async (JsonElement body, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest("Send an object of settings.");
            var changes = new Dictionary<string, string>();
            foreach (var p in body.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                var spec = app.Settings.FirstOrDefault(s => s.Key == p.Name);
                if (spec is null) continue; // unknown names (and showOnDashboard, which is shown but not stored) are ignored
                if (Validate(spec, p.Value) is { } error) return Results.ValidationProblem(new Dictionary<string, string[]> { [p.Name] = [error] });
                changes[p.Name] = spec.Kind == "bool" ? (p.Value.GetBoolean() ? "true" : "false") : spec.Kind == "int" ? p.Value.GetInt32().ToString() : (p.Value.GetString() ?? "").Trim();
            }
            var current = await LoadSettingsAsync(db, app, who.UserId, ct);
            foreach (var (k, v) in changes) current[k] = v;
            await db.GetCollection<AppUserSettings>("appSettings").ReplaceOneAsync(s => s.Id == $"{app.Key}:{who.UserId}",
                new AppUserSettings { Id = $"{app.Key}:{who.UserId}", UserId = who.UserId, AppKey = app.Key, Values = current, UpdatedAtUtc = DateTime.UtcNow }, new ReplaceOptions { IsUpsert = true }, ct);
            return Results.Ok(await ToDtoAsync(db, app, who.UserId, ct));
        });
    }

    private static async Task<Dictionary<string, object?>> ToDtoAsync(IMongoDatabase db, AppInfo app, string userId, CancellationToken ct)
    {
        var values = await LoadSettingsAsync(db, app, userId, ct);
        var dto = app.Settings.ToDictionary(s => s.Key, s => Typed(s, values[s.Key]));
        var account = await LoadAccountAsync(db, userId, app.Key, ct);
        dto["showOnDashboard"] = account is not null && await db.GetCollection<UserDashboardApp>("userDashboardApps").Find(a => a.UserId == userId && a.ConnectionId == account.ConnectionId).AnyAsync(ct);
        return dto;
    }

    // ---- helpers for endpoints -----------------------------------------------------------------

    public static IResult Fail(AppException ex) =>
        Results.Problem(statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : 502, title: ex.Message, extensions: ex.Code is null ? null : new Dictionary<string, object?> { ["code"] = ex.Code });

    public static async Task<IResult> GuardAsync(string appName, Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (AppException ex) { return Fail(ex); }
        catch (CryptographicException) { return Results.Problem(statusCode: 500, title: $"Stored {appName} credentials could not be read. Reconnect {appName}."); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: $"{appName} could not be reached. Please retry."); }
        catch (TaskCanceledException) { return Results.Problem(statusCode: 504, title: $"{appName} took too long to answer. Please retry."); }
    }

    public static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Flag(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    public static int Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    public static JsonElement Obj(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
    public static DateTimeOffset? Date(string? s) => s is not null && DateTimeOffset.TryParse(s, out var d) ? d : null;
    public static string Clip(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Is this app pinned/visible? Used by the assistant to look at an app only when it is connected.</summary>
    public static Task<bool> HasAsync(IMongoDatabase db, string userId, string appKey, CancellationToken ct) => IsConnectedAsync(db, userId, appKey, ct);
}
