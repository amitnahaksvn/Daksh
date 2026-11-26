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

// A small declarative engine for the "one account per person" apps that are mostly REST calls: each app (YouTube, Instagram, Shopify, Home
// Assistant ...) is a ProviderApp definition (how to sign in, what to list, what it may do), and this file supplies everything else:
// the sign-in or credential form, tokens and refresh, encrypted storage, read-only-by-default full-access switch, settings, dashboard tile,
// chat-assistant text and the HTTP endpoints. The endpoints have the same shape as GitHub/Notion/Spotify, so the web app's Manage popup,
// connect popup and dashboard tile work for these apps too.

public enum AuthKind { OAuth, Credentials, ServerKey }

/// <summary>One row of a list: what the screen shows. <see cref="Drill"/> names a view to open with this row's <see cref="Id"/> as its argument.</summary>
public sealed record AppItem(string Id, string Title, string? Subtitle = null, string? Url = null, string? Badge = null, string? Image = null, string? Drill = null, string? Body = null);

/// <summary>A form field. Kind: text, textarea, secret, number, date, datetime, url, select (Options) or select-view (choices come from a view of the same app).</summary>
public sealed record InputSpec(string Key, string Label, string Kind = "text", string? Placeholder = null, bool Required = true, string[]? Options = null, string? OptionsView = null, string? Default = null, string? Hint = null);

/// <summary>A setting shown in Manage. Type: bool, enum, count or text.</summary>
public sealed record SettingRowSpec(string Key, string Label, string Type, string? Hint = null, string? Placeholder = null, string[]? Options = null, string? Default = null);

public sealed record TokenSet(string Access, string? Refresh, DateTimeOffset Expires, Dictionary<string, string>? Extra = null);
public sealed record ProfileInfo(string Id, string Name, Dictionary<string, string>? Extra = null);
public sealed record ActionResult(string Message, string? Url = null);

public sealed class ViewSpec
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public InputSpec[] Inputs { get; init; } = [];
    /// <summary>True when the view is opened from a row of another view and receives that row's id as "arg".</summary>
    public bool TakesArg { get; init; }
    /// <summary>Hidden views are not tabs; they are reached only by drilling in.</summary>
    public bool Hidden { get; init; }
    /// <summary>Offered as the dashboard tile's content (needs no input).</summary>
    public bool Dashboard { get; init; }
    public string? Empty { get; init; }
    public required Func<ProviderCtx, IReadOnlyDictionary<string, string>, Task<IReadOnlyList<AppItem>>> Load { get; init; }
}

public sealed class ActionSpec
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Hint { get; init; }
    public required InputSpec[] Inputs { get; init; }
    public required Func<ProviderCtx, IReadOnlyDictionary<string, string>, Task<ActionResult>> Run { get; init; }
}

public sealed class ExchangeCtx
{
    public required ProviderApp App { get; init; }
    public required OAuthConfig Config { get; init; }
    public required string Code { get; init; }
    public string? Verifier { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Inputs { get; init; }
    public required IHttpClientFactory Clients { get; init; }
    public required IConfiguration Configuration { get; init; }
    public CancellationToken Ct { get; init; }
}

public sealed class OAuthSpec
{
    /// <summary>Settings section: "{Section}:OAuth:ClientId" and so on.</summary>
    public required string Section { get; init; }
    /// <summary>Other sections tried for the client id and secret (YouTube reuses the Google client).</summary>
    public string[] Fallbacks { get; init; } = [];
    public bool SecretOptional { get; init; }
    public required string AuthUrl { get; init; }
    public required string TokenUrl { get; init; }
    public string ClientIdParam { get; init; } = "client_id";
    public string ScopeParam { get; init; } = "scope";
    public string[] ReadScopes { get; init; } = [];
    public string[] WriteScopes { get; init; } = [];
    public string ScopeSeparator { get; init; } = " ";
    public bool Pkce { get; init; }
    /// <summary>Send the client id and secret as HTTP Basic on the token call (instead of in the form).</summary>
    public bool BasicAuth { get; init; }
    public Dictionary<string, string> AuthExtra { get; init; } = [];
    /// <summary>Asked for before sign-in (Shopify's shop name).</summary>
    public InputSpec[] ConnectInputs { get; init; } = [];
    /// <summary>Token response fields copied into the account (for example TikTok's open_id).</summary>
    public string[] TokenExtras { get; init; } = [];
    /// <summary>Query parameters of the callback copied into the account (QuickBooks' realmId, Shopify's shop).</summary>
    public string[] CallbackExtras { get; init; } = [];
    public string CodeParam { get; init; } = "code";
    /// <summary>Providers with one host per country (Zoho): "{region}" in the urls is replaced by "{Section}:Region" or this default.</summary>
    public string DefaultRegion { get; init; } = "com";
    public Func<IConfiguration, OAuthConfig, string, string?, bool, IReadOnlyDictionary<string, string>, string>? BuildAuthUrl { get; init; }
    public Func<ExchangeCtx, Task<TokenSet?>>? Exchange { get; init; }
    public Func<ProviderCtx, TokenSet, Task<TokenSet?>>? Refresh { get; init; }
    /// <summary>Checks the callback before the code is used (Shopify's HMAC). Returns an error reason or null.</summary>
    public Func<OAuthConfig, IReadOnlyDictionary<string, string>, string?>? ValidateCallback { get; init; }
}

public sealed class CredentialSpec
{
    public required InputSpec[] Fields { get; init; }
    /// <summary>Tidies the entered values (adds https://, trims) and rejects bad ones by throwing <see cref="AppException"/>.</summary>
    public Func<Dictionary<string, string>, CancellationToken, Task>? Prepare { get; init; }
    /// <summary>Calls the provider once to prove the values work, and says who the account is.</summary>
    public required Func<ProviderCtx, Task<ProfileInfo>> Probe { get; init; }
}

public sealed class ProviderApp
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required string Color { get; init; }
    /// <summary>What the app does, in a few words (dashboard subtitle).</summary>
    public required string Account { get; init; }
    public required string ConnectTitle { get; init; }
    public required string ConnectBlurb { get; init; }
    public required string ConnectLabel { get; init; }
    public required string WriteLabel { get; init; }
    public required string WriteHint { get; init; }
    public required AuthKind Auth { get; init; }
    public OAuthSpec? OAuth { get; init; }
    public CredentialSpec? Credentials { get; init; }
    /// <summary>For ServerKey apps: the settings that must be present ("GoogleMaps:ApiKey").</summary>
    public string[] ServerKeys { get; init; } = [];
    /// <summary>Does turning on full access need the provider's sign-in again (extra permission), or is it only Daksha's own switch?</summary>
    public bool WriteNeedsSignIn { get; init; }
    /// <summary>False for read-only apps: the full-access switch is not offered.</summary>
    public bool HasWrite { get; init; } = true;
    public required ViewSpec[] Views { get; init; }
    public ActionSpec[] Actions { get; init; } = [];
    public SettingRowSpec[] ExtraSettings { get; init; } = [];
    public string SetupDoc { get; init; } = "Amit.md";
    /// <summary>Steps for whoever sets the app up (where to get keys). Rendered into Amit.md by <see cref="ProviderDocs"/>.</summary>
    public string[] Setup { get; init; } = [];
    /// <summary>Limits worth knowing before relying on the app (plan needed, approvals, what is not supported).</summary>
    public string Notes { get; init; } = "";
    /// <summary>Words in a chat question that make the assistant look at this app.</summary>
    public required Regex Ask { get; init; }
    public Func<ProviderCtx, Task<string?>>? Assistant { get; init; }
    /// <summary>Adds provider-specific headers (and replaces the default bearer token when needed).</summary>
    public Action<HttpRequestMessage, ProviderCtx>? Decorate { get; init; }
    public bool NoBearer { get; init; }
    public Func<IConfiguration, bool>? ConfiguredOverride { get; init; }
    /// <summary>Profile lookup after sign-in (OAuth apps).</summary>
    public Func<ProviderCtx, Task<ProfileInfo>>? Profile { get; init; }

    public string DefaultDashboardView => Views.FirstOrDefault(v => v.Dashboard)?.Id ?? Views.First(v => !v.Hidden).Id;

    public SettingRowSpec[] AllSettings() =>
    [
        new("dashboardView", "What the dashboard tile shows", "enum", "Choose the list shown on your Daksha dashboard.", null, Views.Where(v => v.Dashboard).Select(v => v.Id).ToArray(), DefaultDashboardView),
        new("itemCount", "Items to show", "count", null, null, null, "5"),
        .. ExtraSettings,
    ];

    public bool IsConfigured(IConfiguration c) => ConfiguredOverride?.Invoke(c) ?? Auth switch
    {
        AuthKind.OAuth => ProviderEngine.TryGetOAuthConfig(this, c, out _),
        AuthKind.ServerKey => ServerKeys.All(k => !string.IsNullOrWhiteSpace(c[k])),
        _ => true,
    };

    public AppInfo ToInfo() => new(Key, Name, AllSettings().Select(s => s.Type switch
    {
        "count" => new SettingSpec(s.Key, "int", s.Default ?? "5", 3, 10),
        "bool" => new SettingSpec(s.Key, "bool", s.Default ?? "false"),
        "enum" => new SettingSpec(s.Key, "enum", s.Default ?? s.Options![0], Options: s.Options),
        _ => new SettingSpec(s.Key, "text", s.Default ?? "", Max: 400),
    }).ToArray(), IsConfigured, SoftWrite: !WriteNeedsSignIn);
}

/// <summary>What a view or action gets: the account, its settings, its token or credentials, and a JSON-over-HTTP helper that handles errors.</summary>
public sealed class ProviderCtx
{
    public required ProviderApp App { get; init; }
    public required AppAccountDoc Account { get; init; }
    public required IMongoDatabase Db { get; init; }
    public required IHttpClientFactory Clients { get; init; }
    public required IConfiguration Config { get; init; }
    public required Dictionary<string, string> Settings { get; init; }
    public string? Token { get; set; }
    public Dictionary<string, string> Cred { get; set; } = [];
    public CancellationToken Ct { get; init; }

    public string Extra(string key) => Account.Extra.TryGetValue(key, out var v) ? v : "";
    public string Setting(string key) => Settings.TryGetValue(key, out var v) ? v : "";
    public string Server(string key) => Config[key] ?? "";
    public int Count => Math.Clamp(Int(Settings, "itemCount", 5), 3, 10);

    public async Task<JsonDocument> JsonAsync(HttpMethod method, string url, object? json = null, IEnumerable<KeyValuePair<string, string>>? form = null, IDictionary<string, string>? headers = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (Token is not null && !App.NoBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            App.Decorate?.Invoke(req, this);
            if (headers is not null) foreach (var (k, v) in headers) { req.Headers.Remove(k); req.Headers.TryAddWithoutValidation(k, v); }
            if (json is not null) req.Content = JsonContent.Create(json);
            else if (form is not null) req.Content = new FormUrlEncodedContent(form);
            using var res = await Clients.CreateClient("provider-apps").SendAsync(req, Ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt < 1 && res.Headers.RetryAfter?.Delta is { } d && d <= TimeSpan.FromSeconds(8)) { await Task.Delay(d, Ct); continue; }
                throw new AppException(429, $"{App.Name} is rate limiting requests. Please try again in a moment.", "rate_limited");
            }
            var text = await res.Content.ReadAsStringAsync(Ct);
            if (res.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}");
                try { return JsonDocument.Parse(text); }
                catch (JsonException) { throw new AppException(502, $"{App.Name} sent a reply Daksha could not read."); }
            }
            var detail = ProviderEngine.ErrorDetail(text);
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new AppException(409, $"{App.Name} access expired or was refused. Reconnect {App.Name}.", "reconnect_required"),
                HttpStatusCode.Forbidden => new AppException(403, detail is null ? $"{App.Name} did not allow that. You may need to reconnect with full access, or the account lacks that feature." : $"{App.Name} did not allow that: {detail}", "forbidden"),
                HttpStatusCode.NotFound => new AppException(404, $"{App.Name} could not find that.", "not_found"),
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict => new AppException(400, detail is null ? $"{App.Name} rejected the request." : $"{App.Name} said: {detail}"),
                _ => new AppException(502, detail is null ? $"{App.Name} could not complete the request." : $"{App.Name} could not complete the request: {detail}"),
            };
        }
    }

    public Task<JsonDocument> GetAsync(string url, IDictionary<string, string>? headers = null) => JsonAsync(HttpMethod.Get, url, headers: headers);
    public Task<JsonDocument> PostAsync(string url, object? body = null, IDictionary<string, string>? headers = null) => JsonAsync(HttpMethod.Post, url, body ?? new { }, headers: headers);
    public Task<JsonDocument> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, IDictionary<string, string>? headers = null) => JsonAsync(HttpMethod.Post, url, form: form, headers: headers);
}

/// <summary>Reading JSON without ceremony: dotted paths ("snippet.thumbnails.default.url", "items[0].id"), always returning text or nothing.</summary>
public static class J
{
    public static JsonElement At(JsonElement e, string path)
    {
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw;
            int? index = null;
            var bracket = raw.IndexOf('[');
            if (bracket >= 0 && raw.EndsWith(']') && int.TryParse(raw[(bracket + 1)..^1], out var i)) { name = raw[..bracket]; index = i; }
            if (name.Length > 0)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return default;
            }
            if (index is { } n)
            {
                if (e.ValueKind != JsonValueKind.Array || n >= e.GetArrayLength()) return default;
                e = e[n];
            }
        }
        return e;
    }

    public static string? S(JsonElement e, string path)
    {
        var v = At(e, path);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>First path that has a value.</summary>
    public static string? First(JsonElement e, params string[] paths) { foreach (var p in paths) if (S(e, p) is { Length: > 0 } s) return s; return null; }

    public static IEnumerable<JsonElement> Arr(JsonElement e, string path)
    {
        var v = string.IsNullOrEmpty(path) ? e : At(e, path);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    }

    /// <summary>"{a.b} · {c}" with each {path} replaced by that field of <paramref name="e"/> (missing ones become empty, and stray separators are tidied).</summary>
    public static string T(JsonElement e, string template)
    {
        var text = Regex.Replace(template, @"\{([^}]+)\}", m => S(e, m.Groups[1].Value) ?? "");
        return Regex.Replace(text, @"^[\s·,\-–—|]+|[\s·,\-–—|]+$", "").Replace("·  ·", "·");
    }

    public static string Clip(string? s, int n) => AppConnections.Clip(s, n);
    public static string E(string? s) => Uri.EscapeDataString(s ?? "");
    public static DateTimeOffset? Unix(string? s) => long.TryParse(s, out var n) ? DateTimeOffset.FromUnixTimeSeconds(n) : null;

    /// <summary>"5 Dec, 14:30" in UTC for an ISO or unix-seconds timestamp; empty when unknown.</summary>
    public static string When(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = Unix(s) ?? AppConnections.Date(s);
        return d is null ? "" : d.Value.UtcDateTime.ToString("d MMM yyyy");
    }

    public static IReadOnlyList<AppItem> Items(JsonElement root, string itemsPath, Func<JsonElement, AppItem?> map, int max = 50) =>
        Arr(root, itemsPath).Select(map).Where(i => i is not null).Select(i => i!).Take(max).ToList();
}

public static class HostGuard
{
    /// <summary>Accepts only a plain http(s) address that does not point at link-local or metadata addresses (private LAN addresses are allowed: Home Assistant and Proton Bridge live there).</summary>
    public static async Task<Uri> CheckAsync(string raw, bool allowHttp, CancellationToken ct)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AppException(400, allowHttp ? "Enter a web address starting with http:// or https://." : "Enter a web address starting with https://.");
        await CheckHostAsync(uri.Host, ct);
        return uri;
    }

    public static async Task CheckHostAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains(' ')) throw new AppException(400, "That address is not valid.");
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException) { throw new AppException(400, "Daksha could not find that address."); }
        foreach (var a in addresses)
        {
            var v4 = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
            var b = v4.GetAddressBytes();
            var bad = IPAddress.Any.Equals(v4) || IPAddress.IPv6Any.Equals(v4) || v4.IsIPv6LinkLocal || v4.IsIPv6Multicast
                || (b.Length == 4 && (b[0] == 169 && b[1] == 254 || b[0] >= 224));
            if (bad) throw new AppException(400, "That address is not allowed.");
        }
    }
}

public static class ProviderEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string TokenKey = "provider-oauth-token";
    private const string CredentialKey = "provider-credentials";

    public static IServiceCollection AddProviderApps(this IServiceCollection services)
    {
        services.AddHttpClient("provider-apps", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Daksha", "1.0"));
        });
        return services;
    }

    // ---- configuration -------------------------------------------------------------------------

    public static bool TryGetOAuthConfig(ProviderApp app, IConfiguration config, out OAuthConfig result)
    {
        result = null!;
        var o = app.OAuth;
        if (o is null) return false;
        string? Pick(string leaf) => new[] { o.Section }.Concat(o.Fallbacks).Select(s => config[$"{s}:OAuth:{leaf}"]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var id = Pick("ClientId");
        var secret = Pick("ClientSecret");
        var redirect = config[$"{o.Section}:OAuth:RedirectUri"] ?? $"http://localhost:5080/api/integrations/{app.Key}/callback";
        var frontend = config[$"{o.Section}:OAuth:FrontendUrl"] ?? $"http://localhost:3000/apps/{app.Key}";
        if (string.IsNullOrWhiteSpace(id) || (!o.SecretOptional && string.IsNullOrWhiteSpace(secret)) || !IsAllowedUri(redirect) || !IsAllowedUri(frontend)) return false;
        result = new OAuthConfig(id, string.IsNullOrWhiteSpace(secret) ? null : secret, redirect, frontend);
        return true;
    }

    public static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var text = J.First(r, "error.message", "error_description", "message", "detail", "errors[0].message", "errors[0].detail", "error", "fault.faultstring", "Fault.Error[0].Message", "meta.error_message", "status.message");
            if (text is null && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object) text = errs.GetRawText();
            return string.IsNullOrWhiteSpace(text) ? null : J.Clip(Regex.Replace(text, @"\s+", " "), 220);
        }
        catch (JsonException) { return null; }
    }

    // ---- sign-in -------------------------------------------------------------------------------

    public static string BuildAuthorizationUrl(ProviderApp app, IConfiguration config, OAuthConfig c, string state, string? challenge, bool write, IReadOnlyDictionary<string, string> inputs)
    {
        var o = app.OAuth!;
        if (o.BuildAuthUrl is { } custom) return custom(config, c, state, challenge, write, inputs);
        var q = new Dictionary<string, string?> { [o.ClientIdParam] = c.ClientId, ["response_type"] = "code", ["redirect_uri"] = c.RedirectUri, ["state"] = state };
        var scopes = write ? o.ReadScopes.Concat(o.WriteScopes).Distinct().ToArray() : o.ReadScopes;
        if (scopes.Length > 0) q[o.ScopeParam] = string.Join(o.ScopeSeparator, scopes);
        if (challenge is not null) { q["code_challenge"] = challenge; q["code_challenge_method"] = "S256"; }
        foreach (var (k, v) in o.AuthExtra) q[k] = v;
        return QueryHelpers.AddQueryString(Fill(RegionUrl(o, o.AuthUrl, config), inputs), q);
    }

    public static string RegionUrl(OAuthSpec o, string template, IConfiguration config) =>
        template.Contains("{region}", StringComparison.Ordinal) ? template.Replace("{region}", config[$"{o.Section}:Region"] is { Length: > 0 } r && System.Text.RegularExpressions.Regex.IsMatch(r, "^[a-z.]{2,12}$") ? r : o.DefaultRegion) : template;

    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(template, @"\{(\w+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>One plain call for sign-in hooks that need more than the standard token exchange. Null when the provider says no.</summary>
    public static async Task<JsonDocument?> FetchAsync(IHttpClientFactory clients, HttpMethod method, string url, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct, string? bearer = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (form is not null) req.Content = new FormUrlEncodedContent(form);
        using var res = await clients.CreateClient("provider-apps").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        try { return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)); } catch (JsonException) { return null; }
    }

    private static async Task<TokenSet?> StandardExchangeAsync(ExchangeCtx x)
    {
        var o = x.App.OAuth!;
        var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = x.Code, ["redirect_uri"] = x.Config.RedirectUri };
        if (x.Verifier is not null) form["code_verifier"] = x.Verifier;
        return await TokenCallAsync(x.App, x.Config, x.Configuration, x.Clients, form, null, x.Ct);
    }

    /// <summary>Posts to the token endpoint and reads the standard reply. Null when the provider says no.</summary>
    public static async Task<TokenSet?> TokenCallAsync(ProviderApp app, OAuthConfig c, IConfiguration configuration, IHttpClientFactory clients, Dictionary<string, string> form, string? oldRefresh, CancellationToken ct)
    {
        var o = app.OAuth!;
        using var req = new HttpRequestMessage(HttpMethod.Post, RegionUrl(o, o.TokenUrl, configuration));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (o.BasicAuth) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ClientId}:{c.ClientSecret}")));
        else { form[o.ClientIdParam] = c.ClientId; if (c.ClientSecret is not null) form["client_secret"] = c.ClientSecret; }
        req.Content = new FormUrlEncodedContent(form);
        using var res = await clients.CreateClient("provider-apps").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return ReadTokens(o, doc.RootElement, oldRefresh);
    }

    public static TokenSet? ReadTokens(OAuthSpec o, JsonElement root, string? oldRefresh)
    {
        var access = J.First(root, "access_token", "data.access_token");
        if (string.IsNullOrEmpty(access)) return null;
        var refresh = J.First(root, "refresh_token", "data.refresh_token") ?? oldRefresh;
        var expires = int.TryParse(J.First(root, "expires_in", "data.expires_in"), out var s) && s > 0 ? DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, s - 30)) : DateTimeOffset.UtcNow.AddDays(30);
        var extra = new Dictionary<string, string>();
        foreach (var k in o.TokenExtras) if (J.First(root, k, $"data.{k}") is { Length: > 0 } v) extra[k] = v;
        return new TokenSet(access, refresh, expires, extra);
    }

    private static async Task<IResult> StartOAuthAsync(ProviderApp app, HttpRequest request, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, ISecretProtector protector, CancellationToken ct)
    {
        var o = app.OAuth!;
        if (!TryGetOAuthConfig(app, config, out var c))
            return Results.Problem(statusCode: 503, title: $"{app.Name} sign-in is not configured.", detail: $"Set {o.Section}:OAuth:ClientId and ClientSecret in the API environment. See {app.SetupDoc}.");
        var existing = await LoadAccountAsync(db, who.UserId, app.Key, ct);
        var inputs = new Dictionary<string, string>();
        foreach (var i in o.ConnectInputs)
        {
            var v = (request.Query[i.Key].ToString() ?? "").Trim();
            // Signing in again (for example to turn on full access) keeps what was entered the first time.
            if (v.Length == 0 && existing is not null && existing.Extra.TryGetValue(i.Key, out var kept)) v = kept;
            if (i.Required && v.Length == 0) return Results.Problem(statusCode: 400, title: $"Enter {i.Label.ToLowerInvariant()}.");
            if (v.Length > 200) return Results.Problem(statusCode: 400, title: $"{i.Label} is too long.");
            inputs[i.Key] = v;
        }
        var write = request.Query["write"] == "true";
        var full = app.HasWrite && (write || existing?.Write == true);
        string? verifier = null, challenge = null;
        if (o.Pkce) { verifier = Base64Url(RandomNumberGenerator.GetBytes(48)); challenge = Challenge(verifier); }
        var state = await StartAttemptAsync(db, app.Key, who.UserId, c.FrontendUrl, full, JsonSerializer.Serialize(inputs), verifier is null ? null : protector.Protect(verifier), ct);
        return Results.Ok(new AppAuthorizationResponse(BuildAuthorizationUrl(app, config, c, state, challenge, full, inputs)));
    }

    private static async Task<IResult> CompleteOAuthAsync(ProviderApp app, HttpRequest request, IMongoDatabase db, IConfiguration config, ISecretProtector protector, IHttpClientFactory clients, ILoggerFactory lf, CancellationToken ct)
    {
        var o = app.OAuth!;
        var logger = lf.CreateLogger($"Daksh.Api.{app.Name}");
        var query = request.Query.ToDictionary(k => k.Key, k => k.Value.ToString());
        var attempt = await TakeAttemptAsync(db, app.Key, query.GetValueOrDefault("state"), ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (query.ContainsKey("error")) return Back(attempt.FrontendUrl, app.Key, "consent_denied");
        var code = query.GetValueOrDefault(o.CodeParam);
        if (string.IsNullOrWhiteSpace(code)) return Back(attempt.FrontendUrl, app.Key, "missing_code");
        if (!TryGetOAuthConfig(app, config, out var c)) return Back(attempt.FrontendUrl, app.Key, "oauth_not_configured");
        try
        {
            if (o.ValidateCallback?.Invoke(c, query) is { } bad) { logger.LogWarning("{App} callback rejected: {Reason}", app.Name, bad); return Back(attempt.FrontendUrl, app.Key, "token_exchange"); }
            var inputs = string.IsNullOrEmpty(attempt.Options) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(attempt.Options) ?? [];
            var verifier = attempt.ProtectedCodeVerifier is null ? null : protector.Unprotect(attempt.ProtectedCodeVerifier);
            var x = new ExchangeCtx { App = app, Config = c, Configuration = config, Code = code, Verifier = verifier, Query = query, Inputs = inputs, Clients = clients, Ct = ct };
            var tokens = await (o.Exchange?.Invoke(x) ?? StandardExchangeAsync(x));
            if (tokens is null) { logger.LogWarning("{App} code exchange failed", app.Name); return Back(attempt.FrontendUrl, app.Key, "token_exchange"); }
            var extra = new Dictionary<string, string>(tokens.Extra ?? []);
            foreach (var k in o.CallbackExtras) if (query.TryGetValue(k, out var v) && v.Length > 0) extra[k] = v;
            foreach (var (k, v) in inputs) extra.TryAdd(k, v);
            var probe = new ProviderCtx
            {
                App = app, Db = db, Clients = clients, Config = config, Ct = ct, Token = tokens.Access, Settings = [],
                Account = new AppAccountDoc { ConnectionId = "", UserId = attempt.UserId, AppKey = app.Key, AccountId = "", Extra = extra },
            };
            var profile = await app.Profile!(probe);
            foreach (var (k, v) in profile.Extra ?? []) extra[k] = v;
            var connection = await SaveAccountAsync(db, attempt.UserId, app.Key, profile.Id, profile.Name, attempt.Write, string.Join(' ', attempt.Write ? o.ReadScopes.Concat(o.WriteScopes) : o.ReadScopes), extra, ct);
            await SaveSecretAsync(db, protector, connection.Id, TokenKey, JsonSerializer.Serialize(tokens, JsonOptions), ct);
            return Back(attempt.FrontendUrl, app.Key, "connected");
        }
        catch (AppException ex) { logger.LogWarning("{App} sign-in failed: {Message}", app.Name, ex.Message); return Back(attempt.FrontendUrl, app.Key, ex.Code == "forbidden" ? "consent_denied" : "profile_lookup"); }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach {App}", app.Name); return Back(attempt.FrontendUrl, app.Key, "unavailable"); }
        catch (Exception ex) when (ex is JsonException or CryptographicException) { return Back(attempt.FrontendUrl, app.Key, "token_exchange"); }
    }

    // ---- credentials and server keys -----------------------------------------------------------

    private static async Task<IResult> ConnectWithCredentialsAsync(ProviderApp app, JsonElement body, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var values = new Dictionary<string, string>();
        foreach (var f in app.Credentials!.Fields)
        {
            var v = (Str(body, f.Key) ?? f.Default ?? "").Trim();
            if (f.Required && v.Length == 0) return Results.Problem(statusCode: 400, title: $"Enter {f.Label.ToLowerInvariant()}.");
            if (v.Length > 600) return Results.Problem(statusCode: 400, title: $"{f.Label} is too long.");
            values[f.Key] = v;
        }
        if (app.Credentials.Prepare is { } prepare) await prepare(values, ct);
        var probe = new ProviderCtx
        {
            App = app, Db = db, Clients = clients, Config = config, Ct = ct, Settings = [], Cred = values,
            Account = new AppAccountDoc { ConnectionId = "", UserId = who.UserId, AppKey = app.Key, AccountId = "" },
        };
        var profile = await app.Credentials.Probe(probe);
        var connection = await SaveAccountAsync(db, who.UserId, app.Key, profile.Id, profile.Name, false, "", profile.Extra, ct);
        await SaveSecretAsync(db, protector, connection.Id, CredentialKey, JsonSerializer.Serialize(values, JsonOptions), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ConnectServerKeyAsync(ProviderApp app, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        if (!app.IsConfigured(config))
            return Results.Problem(statusCode: 503, title: $"{app.Name} is not configured.", detail: $"Set {string.Join(", ", app.ServerKeys)} in the API environment. See {app.SetupDoc}.");
        await SaveAccountAsync(db, who.UserId, app.Key, "server", app.Name, false, "", null, ct);
        return Results.NoContent();
    }

    // ---- using an account ----------------------------------------------------------------------

    public static async Task<ProviderCtx> RequireAsync(ProviderApp app, string userId, bool write, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, app.Key, ct) ?? throw new AppException(409, $"Connect {app.Name} to continue.", "not_connected");
        if (write && !account.Write) throw new AppException(403, $"Daksha has read-only access to {app.Name}. Turn on full access in Manage to do this.", "write_required");
        var ctx = new ProviderCtx { App = app, Account = account, Db = db, Clients = clients, Config = config, Ct = ct, Settings = await LoadSettingsAsync(db, app.ToInfo(), userId, ct) };
        switch (app.Auth)
        {
            case AuthKind.OAuth:
                var raw = await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) ?? throw new AppException(409, $"{app.Name} credentials are missing. Reconnect {app.Name}.", "not_connected");
                var tokens = JsonSerializer.Deserialize<TokenSet>(raw, JsonOptions) ?? throw new AppException(409, $"{app.Name} credentials are invalid. Reconnect {app.Name}.", "not_connected");
                if (tokens.Expires <= DateTimeOffset.UtcNow)
                {
                    ctx.Token = tokens.Access;
                    TokenSet? fresh = null;
                    if (app.OAuth!.Refresh is { } custom) fresh = await custom(ctx, tokens);
                    else if (tokens.Refresh is not null && TryGetOAuthConfig(app, config, out var c))
                        fresh = await TokenCallAsync(app, c, config, clients, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh }, tokens.Refresh, ct);
                    if (fresh is null)
                    {
                        await MarkReconnectAsync(db, account.ConnectionId, $"{app.Name} access expired. Reconnect {app.Name}.", ct);
                        throw new AppException(409, $"{app.Name} access expired. Reconnect {app.Name}.", "reconnect_required");
                    }
                    fresh = fresh with { Extra = tokens.Extra };
                    tokens = fresh;
                    await SaveSecretAsync(db, protector, account.ConnectionId, TokenKey, JsonSerializer.Serialize(tokens, JsonOptions), ct);
                }
                ctx.Token = tokens.Access;
                break;
            case AuthKind.Credentials:
                var cred = await LoadSecretAsync(db, protector, account.ConnectionId, CredentialKey, ct) ?? throw new AppException(409, $"{app.Name} details are missing. Reconnect {app.Name}.", "not_connected");
                ctx.Cred = JsonSerializer.Deserialize<Dictionary<string, string>>(cred, JsonOptions) ?? [];
                break;
            case AuthKind.ServerKey:
                if (!app.IsConfigured(config)) throw new AppException(503, $"{app.Name} is not configured on the server.", "not_configured");
                break;
        }
        return ctx;
    }

    // ---- endpoints -----------------------------------------------------------------------------

    public sealed record ViewDto(string Id, string Label, bool TakesArg, bool Hidden, bool Dashboard, string? Empty, InputSpec[] Inputs);
    public sealed record ActionDto(string Id, string Label, string? Hint, InputSpec[] Inputs);
    public sealed record SpecDto(string Key, string Name, string Icon, string Color, string Account, string ConnectTitle, string ConnectBlurb, string ConnectLabel, string WriteLabel, string WriteHint,
        string Auth, bool WriteNeedsSignIn, bool HasWrite, string SetupDoc, InputSpec[] ConnectFields, ViewDto[] Views, ActionDto[] Actions, SettingRowSpec[] Settings);
    public sealed record ActionRequest(Dictionary<string, string>? Inputs);
    public sealed record ActionResponse(string Message, string? Url);

    public static SpecDto ToSpec(ProviderApp a) => new(a.Key, a.Name, a.Icon, a.Color, a.Account, a.ConnectTitle, a.ConnectBlurb, a.ConnectLabel, a.WriteLabel, a.WriteHint,
        a.Auth.ToString().ToLowerInvariant(), a.WriteNeedsSignIn, a.HasWrite, a.SetupDoc,
        a.Auth == AuthKind.OAuth ? a.OAuth!.ConnectInputs : a.Auth == AuthKind.Credentials ? a.Credentials!.Fields : [],
        a.Views.Select(v => new ViewDto(v.Id, v.Label, v.TakesArg, v.Hidden, v.Dashboard, v.Empty, v.Inputs)).ToArray(),
        a.Actions.Select(x => new ActionDto(x.Id, x.Label, x.Hint, x.Inputs)).ToArray(), a.AllSettings());

    public static void MapProviderApps(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/integrations/provider-apps/statuses", async (IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            var keys = ProviderApps.All.Select(a => a.Key).ToArray();
            var connected = await db.GetCollection<IntegrationConnection>("integrationConnections")
                .Find(c => c.UserId == who.UserId && keys.Contains(c.IntegrationKey) && c.Status == ConnectionStatus.Connected).Project(c => c.IntegrationKey).ToListAsync(ct);
            return Results.Ok(keys.ToDictionary(k => k, k => connected.Contains(k)));
        }).RequireAuthorization(AuthSetup.UserScheme).WithTags("Apps");

        foreach (var app in ProviderApps.All) MapApp(routes, app);
    }

    private static void MapApp(IEndpointRouteBuilder routes, ProviderApp app)
    {
        var g = routes.MapGroup($"/api/integrations/{app.Key}").WithTags(app.Name);
        if (app.Auth == AuthKind.OAuth) g.MapGet("/callback", (HttpRequest r, IMongoDatabase db, IConfiguration c, ISecretProtector p, IHttpClientFactory h, ILoggerFactory lf, CancellationToken ct) => CompleteOAuthAsync(app, r, db, c, p, h, lf, ct)).AllowAnonymous();
        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapCommon(app.ToInfo());
        a.MapGet("/spec", () => Results.Ok(ToSpec(app)));

        a.MapPost("/connect", (HttpRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration c, ISecretProtector p, IHttpClientFactory h, CancellationToken ct) => GuardAsync(app.Name, async () =>
        {
            switch (app.Auth)
            {
                case AuthKind.OAuth: return await StartOAuthAsync(app, r, db, who, c, p, ct);
                case AuthKind.Credentials:
                    JsonElement body = default;
                    try { body = await r.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { return Results.Problem(statusCode: 400, title: "Send the details as JSON."); }
                    return await ConnectWithCredentialsAsync(app, body, db, who, c, p, h, ct);
                default: return await ConnectServerKeyAsync(app, db, who, c, ct);
            }
        }));

        a.MapPost("/disconnect", async (IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            var account = await LoadAccountAsync(db, who.UserId, app.Key, ct);
            if (account is not null) await RemoveAsync(db, account, ct);
            return Results.NoContent();
        });

        a.MapGet("/views/{view}", (string view, HttpRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector p, IConfiguration c, IHttpClientFactory h, CancellationToken ct) => GuardAsync(app.Name, async () =>
        {
            var spec = app.Views.FirstOrDefault(v => v.Id == view);
            if (spec is null) return Results.NotFound();
            var inputs = new Dictionary<string, string>();
            foreach (var i in spec.Inputs)
            {
                var v = r.Query[i.Key].ToString().Trim();
                if (v.Length == 0 && i.Default is not null) v = i.Default;
                if (i.Required && v.Length == 0) return Results.Problem(statusCode: 400, title: $"Enter {i.Label.ToLowerInvariant()}.");
                if (v.Length > 500) return Results.Problem(statusCode: 400, title: $"{i.Label} is too long.");
                inputs[i.Key] = v;
            }
            if (spec.TakesArg)
            {
                var arg = r.Query["arg"].ToString().Trim();
                if (arg.Length is 0 or > 300) return Results.Problem(statusCode: 400, title: "Choose an item to open.");
                inputs["arg"] = arg;
            }
            var ctx = await RequireAsync(app, who.UserId, false, db, p, c, h, ct);
            return Results.Ok(await spec.Load(ctx, inputs));
        }));

        a.MapPost("/actions/{action}", (string action, ActionRequest body, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector p, IConfiguration c, IHttpClientFactory h, CancellationToken ct) => GuardAsync(app.Name, async () =>
        {
            var spec = app.Actions.FirstOrDefault(x => x.Id == action);
            if (spec is null) return Results.NotFound();
            var inputs = new Dictionary<string, string>();
            foreach (var i in spec.Inputs)
            {
                var v = (body.Inputs is not null && body.Inputs.TryGetValue(i.Key, out var given) ? given : "").Trim();
                if (v.Length == 0 && i.Default is not null) v = i.Default;
                if (i.Required && v.Length == 0) return Results.Problem(statusCode: 400, title: $"Enter {i.Label.ToLowerInvariant()}.");
                if (v.Length > (i.Kind == "textarea" ? 5000 : 500)) return Results.Problem(statusCode: 400, title: $"{i.Label} is too long.");
                if (i.Kind == "select" && i.Options is not null && v.Length > 0 && !i.Options.Contains(v)) return Results.Problem(statusCode: 400, title: $"Choose a valid {i.Label.ToLowerInvariant()}.");
                inputs[i.Key] = v;
            }
            var ctx = await RequireAsync(app, who.UserId, true, db, p, c, h, ct);
            var result = await spec.Run(ctx, inputs);
            return Results.Ok(new ActionResponse(result.Message, result.Url));
        }));

        a.MapGet("/dashboard", (IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector p, IConfiguration c, IHttpClientFactory h, CancellationToken ct) => DashboardAsync(app, db, who.UserId, p, c, h, ct));
    }

    private static async Task<IResult> DashboardAsync(ProviderApp app, IMongoDatabase db, string userId, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, app.Key, ct);
        if (account is null) return Results.Ok(new AppDashboardDto(false, null, [], []));
        var errors = new List<string>();
        var sections = new List<DashSection>();
        try
        {
            var ctx = await RequireAsync(app, userId, false, db, protector, config, clients, ct);
            var chosen = ctx.Setting("dashboardView");
            var view = app.Views.FirstOrDefault(v => v.Id == chosen && v.Dashboard) ?? app.Views.First(v => v.Id == app.DefaultDashboardView);
            var items = await view.Load(ctx, new Dictionary<string, string>());
            sections.Add(new DashSection(view.Label, $"/apps/{app.Key}", items.Take(ctx.Count).Select(i => new DashItem(i.Id, i.Title, i.Subtitle, i.Url, i.Badge)).ToArray()));
        }
        catch (AppException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add($"{app.Name} could not be reached."); }
        catch (CryptographicException) { errors.Add($"Reconnect {app.Name}."); }
        return Results.Ok(new AppDashboardDto(true, account.AccountName, sections, errors));
    }

    // ---- the chat assistant --------------------------------------------------------------------

    /// <summary>For each connected app the question mentions: a few lines of what it holds. Used by the assistant, never shown raw to others.</summary>
    internal static async Task<(string Text, List<string> Sources)> AssistantAsync(string question, string userId, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var sources = new List<string>();
        foreach (var app in ProviderApps.All.Where(a => a.Ask.IsMatch(question)))
        {
            if (!await IsConnectedAsync(db, userId, app.Key, ct)) continue;
            // One slow provider must not hold up the answer.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var ctx = await RequireAsync(app, userId, false, db, protector, config, clients, limit.Token);
                string? text;
                if (app.Assistant is { } custom) text = await custom(ctx);
                else
                {
                    var view = app.Views.First(v => v.Id == app.DefaultDashboardView);
                    var items = await view.Load(ctx, new Dictionary<string, string>());
                    text = items.Count == 0 ? null : $"{app.Name} — {view.Label}:\n" + string.Join("\n", items.Take(8).Select(i => $"- {J.Clip(i.Title, 100)}{(string.IsNullOrEmpty(i.Subtitle) ? "" : " (" + J.Clip(i.Subtitle, 100) + ")")}"));
                }
                if (!string.IsNullOrWhiteSpace(text)) { sb.AppendLine().AppendLine(text); sources.Add(app.Name); }
            }
            catch (AppException) { }
            catch (HttpRequestException) { }
            catch (CryptographicException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return (sb.ToString(), sources);
    }
}
