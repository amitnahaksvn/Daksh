using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;
using static Daksh.Api.Modules.AppConnections;

namespace Daksh.Api.Modules;

// Notion: a public-integration OAuth sign-in. The person picks which pages and databases to share with Daksha on Notion's consent
// screen, and Daksha sees only those. What Daksha may do (read, update, insert) is set on the integration in Notion; "full access" here
// is Daksha's own switch for creating pages and adding text, so it needs no new sign-in.

public sealed record NotionObjectDto(string Id, string Title, string Kind, string? Url, string? Icon, DateTimeOffset? Edited);
public sealed record NotionPageDto(string Id, string Title, string? Url, string Text, DateTimeOffset? Edited);
public sealed record NotionRowDto(string Id, string Title, string? Url, IReadOnlyDictionary<string, string> Properties);
public sealed record NotionDatabaseDto(string Id, string Title, IReadOnlyList<NotionRowDto> Rows);
public sealed record CreateNotionPageRequest(string? ParentId, string? Title, string? Text);
public sealed record AppendNotionRequest(string? Text);

public static class NotionModule
{
    public const string Key = "notion";
    private const string TokenKey = "notion-oauth-token";
    private const string ApiVersion = "2022-06-28";
    private static readonly Regex IdPattern = new("^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    public static readonly AppInfo Info = new(Key, "Notion",
    [
        new("itemCount", "int", "5", 3, 10),
        new("dashboardSource", "enum", "pages", Options: ["pages", "databases"]),
        new("textLimit", "enum", "20000", Options: ["5000", "20000", "50000"]),
    ], c => TryGetConfig(c, out _), SoftWrite: true);

    private static bool TryGetConfig(IConfiguration c, out OAuthConfig cfg) => AppConnections.TryGetConfig(c, "Notion", "notion", secretOptional: false, out cfg);

    public static IServiceCollection AddNotionIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("notion-api", c => { c.BaseAddress = new Uri("https://api.notion.com/v1/"); c.DefaultRequestHeaders.Add("Notion-Version", ApiVersion); });
        return services;
    }

    public static void MapNotion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/notion").WithTags("Notion");
        g.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();
        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapCommon(Info);
        a.MapPost("/connect", StartOAuthAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapGet("/search", SearchEndpointAsync);
        a.MapGet("/pages/{id}", GetPageAsync);
        a.MapPost("/pages", CreatePageAsync);
        a.MapPost("/pages/{id}/append", AppendAsync);
        a.MapGet("/databases/{id}", QueryDatabaseAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- sign-in -------------------------------------------------------------------------------

    public static string BuildAuthorizationUrl(OAuthConfig c, string state) =>
        QueryHelpers.AddQueryString("https://api.notion.com/v1/oauth/authorize", new Dictionary<string, string?>
        { ["client_id"] = c.ClientId, ["response_type"] = "code", ["owner"] = "user", ["redirect_uri"] = c.RedirectUri, ["state"] = state });

    private static async Task<IResult> StartOAuthAsync(bool? write, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        if (!TryGetConfig(config, out var c))
            return Results.Problem(statusCode: 503, title: "Notion sign-in is not configured.", detail: "Set Notion:OAuth:ClientId and Notion:OAuth:ClientSecret in the API environment. See docs/integrations/notion.md.");
        var existing = await LoadAccountAsync(db, who.UserId, Key, ct);
        var state = await StartAttemptAsync(db, Key, who.UserId, c.FrontendUrl, write == true || existing?.Write == true, "", null, ct);
        return Results.Ok(new AppAuthorizationResponse(BuildAuthorizationUrl(c, state)));
    }

    private static async Task<IResult> CompleteOAuthAsync(string? code, string? state, string? error, IMongoDatabase db, IConfiguration config, ISecretProtector protector, IHttpClientFactory clients, ILoggerFactory lf, CancellationToken ct)
    {
        var logger = lf.CreateLogger("Daksh.Api.Notion");
        var attempt = await TakeAttemptAsync(db, Key, state, ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error)) return Back(attempt.FrontendUrl, Key, "consent_denied");
        if (string.IsNullOrWhiteSpace(code)) return Back(attempt.FrontendUrl, Key, "missing_code");
        if (!TryGetConfig(config, out var c)) return Back(attempt.FrontendUrl, Key, "oauth_not_configured");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth/token") { Content = JsonContent.Create(new { grant_type = "authorization_code", code, redirect_uri = c.RedirectUri }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ClientId}:{c.ClientSecret}")));
            using var res = await clients.CreateClient("notion-api").SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) { logger.LogWarning("Notion code exchange returned HTTP {Status}", (int)res.StatusCode); return Back(attempt.FrontendUrl, Key, "token_exchange"); }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (Str(doc.RootElement, "access_token") is not { Length: > 0 } token) return Back(attempt.FrontendUrl, Key, "token_missing");
            var workspaceId = Str(doc.RootElement, "workspace_id") ?? Str(doc.RootElement, "bot_id") ?? "";
            var workspace = Str(doc.RootElement, "workspace_name") ?? "Notion";
            var owner = Str(Obj(Obj(Obj(doc.RootElement, "owner"), "user"), "person"), "email") ?? Str(Obj(Obj(doc.RootElement, "owner"), "user"), "name") ?? "";
            if (workspaceId.Length == 0) return Back(attempt.FrontendUrl, Key, "profile_lookup");
            var connection = await SaveAccountAsync(db, attempt.UserId, Key, workspaceId, workspace, attempt.Write, "", new() { ["owner"] = owner }, ct);
            await SaveSecretAsync(db, protector, connection.Id, TokenKey, token, ct);
            return Back(attempt.FrontendUrl, Key, "connected");
        }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach Notion"); return Back(attempt.FrontendUrl, Key, "unavailable"); }
        catch (JsonException) { return Back(attempt.FrontendUrl, Key, "token_exchange"); }
    }

    /// <summary>Notion has no API to revoke a token: Daksha forgets it, and the person can remove the integration under Settings → Connections in Notion.</summary>
    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is not null) await RemoveAsync(db, account, ct);
        return Results.NoContent();
    }

    // ---- calling Notion ------------------------------------------------------------------------

    private sealed record Ctx(AppAccountDoc Account, string Token, IMongoDatabase Db, IHttpClientFactory Clients, Dictionary<string, string> Settings);

    private static async Task<Ctx> RequireAsync(string userId, bool write, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, Key, ct) ?? throw new AppException(409, "Connect Notion to continue.", "not_connected");
        if (write && !account.Write) throw new AppException(403, "Daksha has read-only access to Notion. Turn on full access in Manage to create pages and add text.", "write_required");
        var token = await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) ?? throw new AppException(409, "Notion credentials are missing. Reconnect Notion.", "not_connected");
        return new Ctx(account, token, db, clients, await LoadSettingsAsync(db, Info, userId, ct));
    }

    private static async Task<JsonDocument> NotionAsync(Ctx c, string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await c.Clients.CreateClient("notion-api").SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2) { await Task.Delay(res.Headers.RetryAfter?.Delta is { } d && d < TimeSpan.FromSeconds(10) ? d : TimeSpan.FromSeconds(2), ct); continue; }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                await MarkReconnectAsync(c.Db, c.Account.ConnectionId, "Notion access was revoked. Reconnect Notion.", ct);
                throw new AppException(409, "Notion access was revoked. Reconnect Notion.", "reconnect_required");
            }
            if (res.StatusCode == HttpStatusCode.Forbidden) throw new AppException(403, "Your Notion integration is not allowed to do that. Check its capabilities (read, update, insert content) in Notion.", "forbidden");
            if (res.StatusCode == HttpStatusCode.NotFound) throw new AppException(404, "Notion could not find that. Share the page with the Daksha integration in Notion first.", "not_found");
            if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new AppException(429, "Notion is rate limiting requests. Please try again in a moment.", "rate_limited");
            if (!res.IsSuccessStatusCode) throw new AppException(res.StatusCode == HttpStatusCode.BadRequest ? 400 : 502, "Notion could not complete the request.");
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }

    private static string RichText(JsonElement rich) =>
        rich.ValueKind == JsonValueKind.Array ? string.Concat(rich.EnumerateArray().Select(r => Str(r, "plain_text") ?? "")) : "";

    private static string TitleOf(JsonElement obj)
    {
        if (Str(obj, "object") == "database") return RichText(Obj(obj, "title")) is { Length: > 0 } t ? t : "Untitled database";
        var props = Obj(obj, "properties");
        if (props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                if (Str(p.Value, "type") == "title") return RichText(Obj(p.Value, "title")) is { Length: > 0 } t ? t : "Untitled";
        return "Untitled";
    }

    private static NotionObjectDto ToObject(JsonElement o)
    {
        var icon = Obj(o, "icon");
        return new NotionObjectDto(Str(o, "id") ?? "", TitleOf(o), Str(o, "object") ?? "page", Str(o, "url"), Str(icon, "emoji"), Date(Str(o, "last_edited_time")));
    }

    private static async Task<List<NotionObjectDto>> SearchAsync(Ctx c, string? query, string? kind, int count, CancellationToken ct)
    {
        object body = new Dictionary<string, object?>
        {
            ["query"] = string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            ["page_size"] = Math.Clamp(count, 1, 50),
            ["sort"] = new { direction = "descending", timestamp = "last_edited_time" },
            ["filter"] = kind is "page" or "database" ? new { property = "object", value = kind } : null,
        };
        using var doc = await NotionAsync(c, "search", HttpMethod.Post, body, ct);
        return doc.RootElement.TryGetProperty("results", out var r) ? r.EnumerateArray().Select(ToObject).ToList() : [];
    }

    private static Task<IResult> SearchEndpointAsync(string? q, string? kind, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Notion", async () =>
    {
        if (q is { Length: > 200 }) return Results.BadRequest("Search is too long.");
        if (kind is not null && kind is not ("page" or "database" or "all")) return Results.BadRequest("Unknown type.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        return Results.Ok(await SearchAsync(c, q, kind, 30, ct));
    });

    private static string BlockText(JsonElement block)
    {
        var type = Str(block, "type") ?? "";
        var body = Obj(block, type);
        var text = RichText(Obj(body, "rich_text"));
        return type switch
        {
            "heading_1" => "# " + text, "heading_2" => "## " + text, "heading_3" => "### " + text,
            "bulleted_list_item" => "• " + text, "numbered_list_item" => "1. " + text,
            "to_do" => (Flag(body, "checked") ? "[x] " : "[ ] ") + text,
            "quote" => "> " + text, "callout" => text, "code" => "```\n" + text + "\n```", "divider" => "---",
            "child_page" => "📄 " + (Str(body, "title") ?? ""), "child_database" => "🗂 " + (Str(body, "title") ?? ""),
            "paragraph" or "toggle" => text,
            _ => text,
        };
    }

    private static Task<IResult> GetPageAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Notion", async () =>
    {
        if (!IdPattern.IsMatch(id)) return Results.BadRequest("That page is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var page = await NotionAsync(c, $"pages/{id}", ct: ct);
        var sb = new StringBuilder();
        string? cursor = null;
        var limit = Int(c.Settings, "textLimit", 20000);
        for (var i = 0; i < 5 && sb.Length < limit; i++)
        {
            using var blocks = await NotionAsync(c, $"blocks/{id}/children?page_size=100{(cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor))}", ct: ct);
            if (blocks.RootElement.TryGetProperty("results", out var rs)) foreach (var b in rs.EnumerateArray()) sb.AppendLine(BlockText(b));
            cursor = Flag(blocks.RootElement, "has_more") ? Str(blocks.RootElement, "next_cursor") : null;
            if (cursor is null) break;
        }
        var text = sb.ToString();
        if (text.Length > limit) text = text[..limit];
        return Results.Ok(new NotionPageDto(id, TitleOf(page.RootElement), Str(page.RootElement, "url"), text, Date(Str(page.RootElement, "last_edited_time"))));
    });

    private static IEnumerable<object> Paragraphs(string text) =>
        text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Take(90).Select(line => (object)new
        {
            @object = "block", type = "paragraph",
            paragraph = new { rich_text = new[] { new { type = "text", text = new { content = line.Length > 1900 ? line[..1900] : line } } } },
        });

    private static Task<IResult> CreatePageAsync(CreateNotionPageRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Notion", async () =>
    {
        var title = r.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200) return Results.Problem(statusCode: 400, title: "Give the page a title (up to 200 characters).");
        if (r.ParentId is null || !IdPattern.IsMatch(r.ParentId)) return Results.Problem(statusCode: 400, title: "Choose the page to create it under.");
        if (r.Text is { Length: > 50000 }) return Results.Problem(statusCode: 400, title: "The text is too long.");
        var c = await RequireAsync(who.UserId, true, db, protector, clients, ct);
        var body = new
        {
            parent = new { page_id = r.ParentId },
            properties = new { title = new { title = new[] { new { text = new { content = title } } } } },
            children = Paragraphs(r.Text ?? "").ToArray(),
        };
        using var doc = await NotionAsync(c, "pages", HttpMethod.Post, body, ct);
        return Results.Ok(new NotionPageDto(Str(doc.RootElement, "id") ?? "", title, Str(doc.RootElement, "url"), r.Text ?? "", DateTimeOffset.UtcNow));
    });

    private static Task<IResult> AppendAsync(string id, AppendNotionRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Notion", async () =>
    {
        if (!IdPattern.IsMatch(id)) return Results.BadRequest("That page is not valid.");
        if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > 50000) return Results.Problem(statusCode: 400, title: "Enter the text to add (up to 50,000 characters).");
        var c = await RequireAsync(who.UserId, true, db, protector, clients, ct);
        using var _ = await NotionAsync(c, $"blocks/{id}/children", HttpMethod.Patch, new { children = Paragraphs(r.Text).ToArray() }, ct);
        return Results.NoContent();
    });

    private static string PropertyText(JsonElement p)
    {
        var type = Str(p, "type") ?? "";
        var v = Obj(p, type);
        return type switch
        {
            "title" or "rich_text" => RichText(v),
            "number" => p.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : "",
            "select" or "status" => Str(v, "name") ?? "",
            "multi_select" => p.TryGetProperty("multi_select", out var ms) ? string.Join(", ", ms.EnumerateArray().Select(x => Str(x, "name") ?? "")) : "",
            "date" => Str(v, "start") ?? "",
            "checkbox" => Flag(p, "checkbox") ? "✓" : "",
            "url" or "email" or "phone_number" => Str(p, type) ?? "",
            "people" => p.TryGetProperty("people", out var pp) ? string.Join(", ", pp.EnumerateArray().Select(x => Str(x, "name") ?? "")) : "",
            _ => "",
        };
    }

    private static Task<IResult> QueryDatabaseAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Notion", async () =>
    {
        if (!IdPattern.IsMatch(id)) return Results.BadRequest("That database is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var meta = await NotionAsync(c, $"databases/{id}", ct: ct);
        using var rows = await NotionAsync(c, $"databases/{id}/query", HttpMethod.Post, new { page_size = 25 }, ct);
        var list = rows.RootElement.TryGetProperty("results", out var rs) ? rs.EnumerateArray().Select(row =>
        {
            var props = new Dictionary<string, string>();
            foreach (var p in Obj(row, "properties").EnumerateObject())
            {
                if (Str(p.Value, "type") == "title") continue;
                var text = PropertyText(p.Value);
                if (text.Length > 0 && props.Count < 6) props[p.Name] = Clip(text, 80);
            }
            return new NotionRowDto(Str(row, "id") ?? "", TitleOf(row), Str(row, "url"), props);
        }).ToArray() : [];
        return Results.Ok(new NotionDatabaseDto(id, TitleOf(meta.RootElement), list));
    });

    // ---- dashboard and assistant ---------------------------------------------------------------

    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is null) return Results.Ok(new AppDashboardDto(false, null, [], []));
        var errors = new List<string>();
        var sections = new List<DashSection>();
        try
        {
            var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
            var kind = Text(c.Settings, "dashboardSource") == "databases" ? "database" : "page";
            var items = await SearchAsync(c, null, kind, Int(c.Settings, "itemCount", 5), ct);
            sections.Add(new DashSection(kind == "page" ? "Recently edited pages" : "Databases", "/apps/notion",
                items.Select(i => new DashItem(i.Id, $"{i.Icon} {i.Title}".Trim(), i.Edited?.ToString("d MMM yyyy"), i.Url, null)).ToArray()));
        }
        catch (AppException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add("Notion could not be reached."); }
        return Results.Ok(new AppDashboardDto(true, account.AccountName, sections, errors));
    }

    /// <summary>Notion pages matching the question's words (or the most recently edited ones), for the chat assistant. Null when Notion is not connected.</summary>
    internal static async Task<string?> AssistantTextAsync(string userId, string query, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(userId, false, db, protector, clients, ct);
            var items = await SearchAsync(c, query, "page", 6, ct);
            var sb = new StringBuilder(query.Length > 0 ? "Notion pages matching the question:\n" : "Recently edited Notion pages:\n");
            foreach (var i in items) sb.AppendLine($"- {i.Title} (edited {i.Edited?.ToString("d MMM yyyy") ?? "?"}) {i.Url}");
            if (items.Count == 0) sb.AppendLine("- none found (only pages shared with the Daksha integration are visible)");
            // Read the best match so a question about its content can be answered.
            if (items.Count > 0 && query.Length > 0)
            {
                using var blocks = await NotionAsync(c, $"blocks/{items[0].Id}/children?page_size=50", ct: ct);
                var text = blocks.RootElement.TryGetProperty("results", out var rs) ? string.Join("\n", rs.EnumerateArray().Select(BlockText).Where(t => t.Length > 0)) : "";
                if (text.Length > 0) sb.AppendLine($"Start of \"{items[0].Title}\":\n{Clip(text, 1500)}");
            }
            return sb.ToString();
        }
        catch (AppException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}
