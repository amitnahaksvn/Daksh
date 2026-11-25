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
using MongoDB.Driver;
using static Daksh.Api.Modules.AppConnections;

namespace Daksh.Api.Modules;

// Spotify: Authorization Code with PKCE (no client secret needed). Read-only by default: see what is playing, recent plays, playlists and
// search. "Full access" adds playback control, queueing and playing a track or playlist (Spotify requires Premium for playback control).
// Access tokens last an hour, so the refresh token is kept encrypted and used to get a new one.

public sealed record SpotifyTrackDto(string Id, string Uri, string Name, string Artists, string Album, string? Image, int DurationMs, DateTimeOffset? PlayedAt);
public sealed record SpotifyNowDto(bool Active, bool IsPlaying, SpotifyTrackDto? Track, int ProgressMs, string? Device, int? Volume, bool Shuffle, string Repeat);
public sealed record SpotifyPlaylistDto(string Id, string Uri, string Name, string? Owner, int Tracks, string? Image, string? Url);
public sealed record SpotifySearchDto(IReadOnlyList<SpotifyTrackDto> Tracks, IReadOnlyList<SpotifyPlaylistDto> Playlists);
public sealed record SpotifyControlRequest(string? Action, int? Value, string? Mode);
public sealed record SpotifyPlayRequest(string? Uri, string? ContextUri);
public sealed record SpotifyQueueRequest(string? Uri);

public static class SpotifyModule
{
    public const string Key = "spotify";
    private const string TokenKey = "spotify-oauth-token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex UriPattern = new(@"^spotify:(track|album|playlist|artist|episode|show):[A-Za-z0-9]{22}$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new("^[A-Za-z0-9]{22}$", RegexOptions.Compiled);

    public static readonly AppInfo Info = new(Key, "Spotify",
    [
        new("itemCount", "int", "5", 3, 10),
        new("showRecent", "bool", "true"),
        new("topRange", "enum", "medium_term", Options: ["short_term", "medium_term", "long_term"]),
    ], c => TryGetConfig(c, out _));

    private static bool TryGetConfig(IConfiguration c, out OAuthConfig cfg) => AppConnections.TryGetConfig(c, "Spotify", "spotify", secretOptional: true, out cfg);

    public static string ScopesFor(bool write)
    {
        var scopes = new List<string> { "user-read-private", "user-read-email", "user-read-playback-state", "user-read-currently-playing", "user-read-recently-played", "user-top-read", "playlist-read-private", "playlist-read-collaborative", "user-library-read" };
        if (write) scopes.AddRange(["user-modify-playback-state", "playlist-modify-private", "playlist-modify-public"]);
        return string.Join(' ', scopes);
    }

    public static IServiceCollection AddSpotifyIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("spotify-accounts", c => c.BaseAddress = new Uri("https://accounts.spotify.com/"));
        services.AddHttpClient("spotify-api", c => c.BaseAddress = new Uri("https://api.spotify.com/v1/"));
        return services;
    }

    public static void MapSpotify(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/spotify").WithTags("Spotify");
        g.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();
        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapCommon(Info);
        a.MapPost("/connect", StartOAuthAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapGet("/now", NowAsync);
        a.MapPost("/control", ControlAsync);
        a.MapPost("/play", PlayAsync);
        a.MapPost("/queue", QueueAsync);
        a.MapGet("/recent", RecentAsync);
        a.MapGet("/top", TopAsync);
        a.MapGet("/playlists", PlaylistsAsync);
        a.MapGet("/playlists/{id}/tracks", PlaylistTracksAsync);
        a.MapGet("/search", SearchAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- sign-in and tokens --------------------------------------------------------------------

    public static string BuildAuthorizationUrl(OAuthConfig c, string state, string challenge, bool write) =>
        QueryHelpers.AddQueryString("https://accounts.spotify.com/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = c.ClientId, ["response_type"] = "code", ["redirect_uri"] = c.RedirectUri, ["scope"] = ScopesFor(write), ["state"] = state,
            ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["show_dialog"] = "true",
        });

    private static async Task<IResult> StartOAuthAsync(bool? write, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, ISecretProtector protector, CancellationToken ct)
    {
        if (!TryGetConfig(config, out var c))
            return Results.Problem(statusCode: 503, title: "Spotify sign-in is not configured.", detail: "Set Spotify:OAuth:ClientId in the API environment. See docs/integrations/spotify.md.");
        var existing = await LoadAccountAsync(db, who.UserId, Key, ct);
        var full = write == true || existing?.Write == true;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = await StartAttemptAsync(db, Key, who.UserId, c.FrontendUrl, full, "", protector.Protect(verifier), ct);
        return Results.Ok(new AppAuthorizationResponse(BuildAuthorizationUrl(c, state, Challenge(verifier), full)));
    }

    private sealed record Tokens(string Access, string Refresh, DateTimeOffset Expires);

    private static async Task<(Tokens? Tokens, string? Scope)> TokenCallAsync(OAuthConfig c, IHttpClientFactory clients, Dictionary<string, string> form, string? oldRefresh, CancellationToken ct)
    {
        form["client_id"] = c.ClientId;
        if (c.ClientSecret is not null) form["client_secret"] = c.ClientSecret;
        using var res = await clients.CreateClient("spotify-accounts").PostAsync("api/token", new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode) return (null, null);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var access = Str(doc.RootElement, "access_token");
        if (string.IsNullOrEmpty(access)) return (null, null);
        var refresh = Str(doc.RootElement, "refresh_token") ?? oldRefresh;
        if (string.IsNullOrEmpty(refresh)) return (null, null);
        return (new Tokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, Num(doc.RootElement, "expires_in") - 30))), Str(doc.RootElement, "scope"));
    }

    private static async Task<IResult> CompleteOAuthAsync(string? code, string? state, string? error, IMongoDatabase db, IConfiguration config, ISecretProtector protector, IHttpClientFactory clients, ILoggerFactory lf, CancellationToken ct)
    {
        var logger = lf.CreateLogger("Daksh.Api.Spotify");
        var attempt = await TakeAttemptAsync(db, Key, state, ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error)) return Back(attempt.FrontendUrl, Key, "consent_denied");
        if (string.IsNullOrWhiteSpace(code) || attempt.ProtectedCodeVerifier is null) return Back(attempt.FrontendUrl, Key, "missing_code");
        if (!TryGetConfig(config, out var c)) return Back(attempt.FrontendUrl, Key, "oauth_not_configured");
        try
        {
            var verifier = protector.Unprotect(attempt.ProtectedCodeVerifier);
            var (tokens, scope) = await TokenCallAsync(c, clients, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = c.RedirectUri, ["code_verifier"] = verifier }, null, ct);
            if (tokens is null) { logger.LogWarning("Spotify code exchange failed"); return Back(attempt.FrontendUrl, Key, "token_exchange"); }
            using var req = new HttpRequestMessage(HttpMethod.Get, "me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
            using var me = await clients.CreateClient("spotify-api").SendAsync(req, ct);
            if (!me.IsSuccessStatusCode) return Back(attempt.FrontendUrl, Key, "profile_lookup");
            using var profile = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
            var id = Str(profile.RootElement, "id") ?? "";
            if (id.Length == 0) return Back(attempt.FrontendUrl, Key, "profile_lookup");
            var name = Str(profile.RootElement, "display_name") ?? id;
            var scopes = scope ?? ScopesFor(attempt.Write);
            var connection = await SaveAccountAsync(db, attempt.UserId, Key, id, name, scopes.Contains("user-modify-playback-state"), scopes, new() { ["product"] = Str(profile.RootElement, "product") ?? "" }, ct);
            await SaveSecretAsync(db, protector, connection.Id, TokenKey, JsonSerializer.Serialize(tokens, JsonOptions), ct);
            return Back(attempt.FrontendUrl, Key, "connected");
        }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach Spotify"); return Back(attempt.FrontendUrl, Key, "unavailable"); }
        catch (Exception ex) when (ex is JsonException or CryptographicException) { return Back(attempt.FrontendUrl, Key, "token_exchange"); }
    }

    /// <summary>Spotify has no API to revoke a token: Daksha forgets it, and the person can remove access at spotify.com/account/apps.</summary>
    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is not null) await RemoveAsync(db, account, ct);
        return Results.NoContent();
    }

    // ---- calling Spotify -----------------------------------------------------------------------

    private sealed record Ctx(AppAccountDoc Account, Tokens Tokens, IMongoDatabase Db, IHttpClientFactory Clients, Dictionary<string, string> Settings);

    private static async Task<Ctx> RequireAsync(string userId, bool write, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, Key, ct) ?? throw new AppException(409, "Connect Spotify to continue.", "not_connected");
        if (write && !account.Write) throw new AppException(403, "Daksha has read-only access to Spotify. Turn on full access in Manage to control playback.", "write_required");
        var raw = await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) ?? throw new AppException(409, "Spotify credentials are missing. Reconnect Spotify.", "not_connected");
        var tokens = JsonSerializer.Deserialize<Tokens>(raw, JsonOptions) ?? throw new AppException(409, "Spotify credentials are invalid. Reconnect Spotify.", "not_connected");
        if (tokens.Expires <= DateTimeOffset.UtcNow)
        {
            if (!TryGetConfig(config, out var c)) throw new AppException(503, "Spotify sign-in is not configured.");
            var (fresh, _) = await TokenCallAsync(c, clients, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh }, tokens.Refresh, ct);
            if (fresh is null)
            {
                await MarkReconnectAsync(db, account.ConnectionId, "Spotify access expired. Reconnect Spotify.", ct);
                throw new AppException(409, "Spotify access expired. Reconnect Spotify.", "reconnect_required");
            }
            tokens = fresh;
            await SaveSecretAsync(db, protector, account.ConnectionId, TokenKey, JsonSerializer.Serialize(tokens, JsonOptions), ct);
        }
        return new Ctx(account, tokens, db, clients, await LoadSettingsAsync(db, Info, userId, ct));
    }

    private static async Task<JsonDocument> SpotifyAsync(Ctx c, string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Tokens.Access);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await c.Clients.CreateClient("spotify-api").SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt < 1 && res.Headers.RetryAfter?.Delta is { } d && d <= TimeSpan.FromSeconds(8)) { await Task.Delay(d, ct); continue; }
                throw new AppException(429, "Spotify is rate limiting requests. Please try again in a moment.", "rate_limited");
            }
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new AppException(409, "Spotify access expired. Reconnect Spotify.", "reconnect_required");
            if (res.StatusCode == HttpStatusCode.Forbidden)
                throw new AppException(403, text.Contains("PREMIUM_REQUIRED", StringComparison.OrdinalIgnoreCase) ? "Spotify only allows playback control with a Premium account." : "Spotify did not allow that. You may need to reconnect with full access.", text.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase) ? "premium_required" : "forbidden");
            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new AppException(404, text.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase) ? "No Spotify device is active. Open Spotify on a phone or computer and start playing something first." : "Spotify could not find that.", text.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase) ? "no_device" : "not_found");
            if (!res.IsSuccessStatusCode) throw new AppException(res.StatusCode == HttpStatusCode.BadRequest ? 400 : 502, "Spotify could not complete the request.");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }

    private static SpotifyTrackDto ToTrack(JsonElement t, DateTimeOffset? playedAt = null)
    {
        var album = Obj(t, "album");
        var images = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array ? im : default;
        var image = images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Str(images[images.GetArrayLength() > 1 ? 1 : 0], "url") : null;
        var artists = t.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array ? string.Join(", ", ar.EnumerateArray().Select(x => Str(x, "name") ?? "")) : "";
        return new SpotifyTrackDto(Str(t, "id") ?? "", Str(t, "uri") ?? "", Str(t, "name") ?? "", artists, Str(album, "name") ?? "", image, Num(t, "duration_ms"), playedAt);
    }

    private static SpotifyPlaylistDto ToPlaylist(JsonElement p)
    {
        var images = p.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array ? im : default;
        return new SpotifyPlaylistDto(Str(p, "id") ?? "", Str(p, "uri") ?? "", Str(p, "name") ?? "", Str(Obj(p, "owner"), "display_name"), Num(Obj(p, "tracks"), "total"),
            images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Str(images[0], "url") : null, Str(Obj(p, "external_urls"), "spotify"));
    }

    private static async Task<SpotifyNowDto> NowCoreAsync(Ctx c, CancellationToken ct)
    {
        using var doc = await SpotifyAsync(c, "me/player?additional_types=track,episode", ct: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("device", out var device)) return new SpotifyNowDto(false, false, null, 0, null, null, false, "off");
        var item = Obj(doc.RootElement, "item");
        return new SpotifyNowDto(true, Flag(doc.RootElement, "is_playing"), item.ValueKind == JsonValueKind.Object ? ToTrack(item) : null, Num(doc.RootElement, "progress_ms"),
            Str(device, "name"), device.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null, Flag(doc.RootElement, "shuffle_state"), Str(doc.RootElement, "repeat_state") ?? "off");
    }

    private static Task<IResult> NowAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        return Results.Ok(await NowCoreAsync(c, ct));
    });

    private static Task<IResult> ControlAsync(SpotifyControlRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        var c = await RequireAsync(who.UserId, true, db, protector, config, clients, ct);
        (HttpMethod Method, string Path)? call = r.Action switch
        {
            "play" => (HttpMethod.Put, "me/player/play"),
            "pause" => (HttpMethod.Put, "me/player/pause"),
            "next" => (HttpMethod.Post, "me/player/next"),
            "previous" => (HttpMethod.Post, "me/player/previous"),
            "volume" when r.Value is >= 0 and <= 100 => (HttpMethod.Put, $"me/player/volume?volume_percent={r.Value}"),
            "seek" when r.Value is >= 0 and <= 36_000_000 => (HttpMethod.Put, $"me/player/seek?position_ms={r.Value}"),
            "shuffle" when r.Mode is "true" or "false" => (HttpMethod.Put, $"me/player/shuffle?state={r.Mode}"),
            "repeat" when r.Mode is "off" or "track" or "context" => (HttpMethod.Put, $"me/player/repeat?state={r.Mode}"),
            _ => null,
        };
        if (call is null) return Results.Problem(statusCode: 400, title: "That playback action is not valid.");
        using var _ = await SpotifyAsync(c, call.Value.Path, call.Value.Method, call.Value.Method == HttpMethod.Put || call.Value.Method == HttpMethod.Post ? new { } : null, ct);
        return Results.NoContent();
    });

    private static Task<IResult> PlayAsync(SpotifyPlayRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        if ((r.Uri is null) == (r.ContextUri is null)) return Results.Problem(statusCode: 400, title: "Choose one track, album or playlist to play.");
        var uri = r.Uri ?? r.ContextUri!;
        if (!UriPattern.IsMatch(uri)) return Results.Problem(statusCode: 400, title: "That is not a valid Spotify link.");
        var c = await RequireAsync(who.UserId, true, db, protector, config, clients, ct);
        object body = r.Uri is not null ? new { uris = new[] { r.Uri } } : new { context_uri = r.ContextUri };
        using var _ = await SpotifyAsync(c, "me/player/play", HttpMethod.Put, body, ct);
        return Results.NoContent();
    });

    private static Task<IResult> QueueAsync(SpotifyQueueRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        if (r.Uri is null || !UriPattern.IsMatch(r.Uri)) return Results.Problem(statusCode: 400, title: "That is not a valid Spotify link.");
        var c = await RequireAsync(who.UserId, true, db, protector, config, clients, ct);
        using var _ = await SpotifyAsync(c, $"me/player/queue?uri={Uri.EscapeDataString(r.Uri)}", HttpMethod.Post, new { }, ct);
        return Results.NoContent();
    });

    private static Task<IResult> RecentAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        return Results.Ok(await RecentCoreAsync(c, 25, ct));
    });

    private static async Task<List<SpotifyTrackDto>> RecentCoreAsync(Ctx c, int limit, CancellationToken ct)
    {
        using var doc = await SpotifyAsync(c, $"me/player/recently-played?limit={Math.Clamp(limit, 1, 50)}", ct: ct);
        return doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(i => ToTrack(Obj(i, "track"), Date(Str(i, "played_at")))).ToList() : [];
    }

    private static Task<IResult> TopAsync(string? range, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        if (range is not null && range is not ("short_term" or "medium_term" or "long_term")) return Results.BadRequest("Unknown range.");
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        using var doc = await SpotifyAsync(c, $"me/top/tracks?limit=25&time_range={range ?? Text(c.Settings, "topRange")}", ct: ct);
        return Results.Ok(doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(t => ToTrack(t)).ToArray() : []);
    });

    private static Task<IResult> PlaylistsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        using var doc = await SpotifyAsync(c, "me/playlists?limit=50", ct: ct);
        return Results.Ok(doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(ToPlaylist).ToArray() : []);
    });

    private static Task<IResult> PlaylistTracksAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        if (!IdPattern.IsMatch(id)) return Results.BadRequest("That playlist is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        using var doc = await SpotifyAsync(c, $"playlists/{id}/tracks?limit=50&fields=items(track(id,uri,name,duration_ms,artists(name),album(name,images)))", ct: ct);
        return Results.Ok(doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(i => Obj(i, "track")).Where(t => t.ValueKind == JsonValueKind.Object).Select(t => ToTrack(t)).ToArray() : []);
    });

    private static Task<IResult> SearchAsync(string? q, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Spotify", async () =>
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Results.BadRequest("Enter something to search for (up to 200 characters).");
        var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
        using var doc = await SpotifyAsync(c, $"search?q={Uri.EscapeDataString(q.Trim())}&type=track,playlist&limit=12", ct: ct);
        var tracks = Obj(Obj(doc.RootElement, "tracks"), "items") is { ValueKind: JsonValueKind.Array } ti ? ti.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(t => ToTrack(t)).ToArray() : [];
        var playlists = Obj(Obj(doc.RootElement, "playlists"), "items") is { ValueKind: JsonValueKind.Array } pi ? pi.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(ToPlaylist).ToArray() : [];
        return Results.Ok(new SpotifySearchDto(tracks, playlists));
    });

    // ---- dashboard and assistant ---------------------------------------------------------------

    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is null) return Results.Ok(new AppDashboardDto(false, null, [], []));
        var errors = new List<string>();
        var sections = new List<DashSection>();
        try
        {
            var c = await RequireAsync(who.UserId, false, db, protector, config, clients, ct);
            var now = await NowCoreAsync(c, ct);
            sections.Add(new DashSection(now.IsPlaying ? "Now playing" : "Player", "/apps/spotify",
                now.Track is null ? [] : [new DashItem(now.Track.Id, now.Track.Name, $"{now.Track.Artists} · {now.Device}", null, now.IsPlaying ? "playing" : "paused")]));
            if (Bool(c.Settings, "showRecent"))
            {
                var recent = await RecentCoreAsync(c, Int(c.Settings, "itemCount", 5), ct);
                sections.Add(new DashSection("Recently played", "/apps/spotify?tab=recent", recent.Select(t => new DashItem($"{t.Id}-{t.PlayedAt?.ToUnixTimeSeconds()}", t.Name, t.Artists, null, null)).ToArray()));
            }
        }
        catch (AppException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add("Spotify could not be reached."); }
        return Results.Ok(new AppDashboardDto(true, account.AccountName, sections, errors));
    }

    /// <summary>What is playing and what was played recently, for the chat assistant. Null when Spotify is not connected.</summary>
    internal static async Task<string?> AssistantTextAsync(string userId, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(userId, false, db, protector, config, clients, ct);
            var now = await NowCoreAsync(c, ct);
            var sb = new StringBuilder();
            sb.AppendLine(now.Track is null ? "Spotify: nothing is playing right now." : $"Spotify {(now.IsPlaying ? "is playing" : "is paused on")}: {now.Track.Name} by {now.Track.Artists} on {now.Device}.");
            var recent = await RecentCoreAsync(c, 8, ct);
            if (recent.Count > 0) { sb.AppendLine("Recently played on Spotify (newest first):"); foreach (var t in recent) sb.AppendLine($"- {t.Name} — {t.Artists}"); }
            return sb.ToString();
        }
        catch (AppException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}
