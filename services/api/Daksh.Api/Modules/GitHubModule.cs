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

// GitHub: an OAuth App sign-in (a user token, so Daksha acts as the person). Read-only by default; "private repositories" adds the repo
// scope; "full access" lets Daksha comment on issues and pull requests and mark notifications read.

public sealed record GhItemDto(string Key, string Repo, int Number, string Title, string State, bool IsPr, bool Draft, string Author, string Url, int Comments, DateTimeOffset? Updated, IReadOnlyList<string> Labels);
public sealed record GhNotificationDto(string Id, string Repo, string Title, string Reason, string Type, bool Unread, string? Url, DateTimeOffset? Updated);
public sealed record GhRepoDto(string FullName, string? Description, bool Private, string? Language, int Stars, int OpenIssues, DateTimeOffset? Pushed, string Url);
public sealed record GhCommentDto(string Author, string Body, DateTimeOffset? Created, string? Url);
public sealed record GhItemDetailDto(GhItemDto Item, string Body, IReadOnlyList<GhCommentDto> Comments);
public sealed record GhCommentRequest(string? Body);

public static class GitHubModule
{
    public const string Key = "github";
    private const string TokenKey = "github-oauth-token";
    private static readonly Regex NamePattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);

    public static readonly AppInfo Info = new(Key, "GitHub",
    [
        new("itemCount", "int", "5", 3, 10),
        new("dashboardSource", "enum", "review", Options: ["review", "assigned", "created", "mentioned"]),
        new("includeIssues", "bool", "true"),
        new("includeNotifications", "bool", "true"),
        new("hideDrafts", "bool", "false"),
    ], c => TryGetConfig(c, out _));

    private static bool TryGetConfig(IConfiguration c, out OAuthConfig cfg) => AppConnections.TryGetConfig(c, "GitHub", "github", secretOptional: false, out cfg);

    public static IServiceCollection AddGitHubIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("github-oauth", c => { c.BaseAddress = new Uri("https://github.com/"); c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); });
        services.AddHttpClient("github-api", c =>
        {
            c.BaseAddress = new Uri("https://api.github.com/");
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Daksha", "1.0"));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        return services;
    }

    public static void MapGitHub(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/github").WithTags("GitHub");
        g.MapGet("/callback", CompleteOAuthAsync).AllowAnonymous();
        var a = g.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);
        a.MapCommon(Info);
        a.MapPost("/connect", StartOAuthAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapGet("/items", ListItemsAsync);
        a.MapGet("/items/{owner}/{repo}/{number:int}", ItemAsync);
        a.MapPost("/items/{owner}/{repo}/{number:int}/comments", CommentAsync);
        a.MapGet("/notifications", NotificationsAsync);
        a.MapPost("/notifications/read", MarkReadAsync);
        a.MapGet("/repos", ReposAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- sign-in -------------------------------------------------------------------------------

    public static string ScopesFor(bool write, bool priv)
    {
        var scopes = new List<string> { "read:user", "notifications", "read:org" };
        if (priv) scopes.Add("repo");          // GitHub has no read-only scope for private repositories
        else if (write) scopes.Add("public_repo");
        return string.Join(' ', scopes);
    }

    public static string BuildAuthorizationUrl(OAuthConfig c, string state, bool write, bool priv) =>
        QueryHelpers.AddQueryString("https://github.com/login/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = c.ClientId, ["redirect_uri"] = c.RedirectUri, ["scope"] = ScopesFor(write, priv), ["state"] = state, ["allow_signup"] = "false",
        });

    private static async Task<IResult> StartOAuthAsync(bool? write, bool? @private, IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        if (!TryGetConfig(config, out var c))
            return Results.Problem(statusCode: 503, title: "GitHub sign-in is not configured.", detail: "Set GitHub:OAuth:ClientId and GitHub:OAuth:ClientSecret in the API environment. See docs/integrations/github.md.");
        var existing = await LoadAccountAsync(db, who.UserId, Key, ct);
        var priv = @private == true || existing?.Scopes.Split(' ').Contains("repo") == true; // a permission once granted is asked for again so reconnecting never lowers it
        var full = write == true || existing?.Write == true;
        var state = await StartAttemptAsync(db, Key, who.UserId, c.FrontendUrl, full, priv ? "private" : "", null, ct);
        return Results.Ok(new AppAuthorizationResponse(BuildAuthorizationUrl(c, state, full, priv)));
    }

    private static async Task<IResult> CompleteOAuthAsync(string? code, string? state, string? error, IMongoDatabase db, IConfiguration config, ISecretProtector protector, IHttpClientFactory clients, ILoggerFactory lf, CancellationToken ct)
    {
        var logger = lf.CreateLogger("Daksh.Api.GitHub");
        var attempt = await TakeAttemptAsync(db, Key, state, ct);
        if (attempt is null) return Results.BadRequest("OAuth state is invalid or expired.");
        if (!string.IsNullOrWhiteSpace(error)) return Back(attempt.FrontendUrl, Key, "consent_denied");
        if (string.IsNullOrWhiteSpace(code)) return Back(attempt.FrontendUrl, Key, "missing_code");
        if (!TryGetConfig(config, out var c)) return Back(attempt.FrontendUrl, Key, "oauth_not_configured");
        try
        {
            string token, scopes;
            using (var res = await clients.CreateClient("github-oauth").PostAsync("login/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = c.ClientId, ["client_secret"] = c.ClientSecret!, ["code"] = code, ["redirect_uri"] = c.RedirectUri }), ct))
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (Str(doc.RootElement, "access_token") is not { Length: > 0 } t) { logger.LogWarning("GitHub code exchange failed: {Error}", Str(doc.RootElement, "error")); return Back(attempt.FrontendUrl, Key, "token_exchange"); }
                token = t; scopes = (Str(doc.RootElement, "scope") ?? "").Replace(',', ' ');
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var me = await clients.CreateClient("github-api").SendAsync(req, ct);
            if (!me.IsSuccessStatusCode) return Back(attempt.FrontendUrl, Key, "profile_lookup");
            using var profile = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
            var login = Str(profile.RootElement, "login") ?? "";
            if (login.Length == 0) return Back(attempt.FrontendUrl, Key, "profile_lookup");
            var connection = await SaveAccountAsync(db, attempt.UserId, Key, login, login, attempt.Write && (scopes.Contains("public_repo") || scopes.Split(' ').Contains("repo")), scopes,
                new() { ["name"] = Str(profile.RootElement, "name") ?? login, ["avatar"] = Str(profile.RootElement, "avatar_url") ?? "" }, ct);
            await SaveSecretAsync(db, protector, connection.Id, TokenKey, token, ct);
            return Back(attempt.FrontendUrl, Key, "connected");
        }
        catch (HttpRequestException ex) { logger.LogError(ex, "Could not reach GitHub"); return Back(attempt.FrontendUrl, Key, "unavailable"); }
        catch (JsonException) { return Back(attempt.FrontendUrl, Key, "token_exchange"); }
    }

    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is null) return Results.NoContent();
        // Revoke the grant at GitHub first (best effort), so disconnecting really cuts access.
        try
        {
            if (TryGetConfig(config, out var c) && await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) is { } token)
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"applications/{c.ClientId}/grant") { Content = JsonContent.Create(new { access_token = token }) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ClientId}:{c.ClientSecret}")));
                using var _ = await clients.CreateClient("github-api").SendAsync(req, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Security.Cryptography.CryptographicException or TaskCanceledException) { }
        await RemoveAsync(db, account, ct);
        return Results.NoContent();
    }

    // ---- calling GitHub ------------------------------------------------------------------------

    private sealed record Ctx(AppAccountDoc Account, string Token, IMongoDatabase Db, IHttpClientFactory Clients);

    private static async Task<Ctx> RequireAsync(string userId, bool write, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, Key, ct) ?? throw new AppException(409, "Connect GitHub to continue.", "not_connected");
        if (write && !account.Write) throw new AppException(403, "Daksha has read-only access to GitHub. Turn on full access in Manage to comment.", "write_required");
        var token = await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) ?? throw new AppException(409, "GitHub credentials are missing. Reconnect GitHub.", "not_connected");
        return new Ctx(account, token, db, clients);
    }

    private static async Task<JsonDocument> GhAsync(Ctx c, string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await c.Clients.CreateClient("github-api").SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            await MarkReconnectAsync(c.Db, c.Account.ConnectionId, "GitHub access was revoked. Reconnect GitHub.", ct);
            throw new AppException(409, "GitHub access was revoked or has expired. Reconnect GitHub.", "reconnect_required");
        }
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new AppException(res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0" ? 429 : 403,
                res.Headers.TryGetValues("X-RateLimit-Remaining", out var r2) && r2.FirstOrDefault() == "0" ? "GitHub is rate limiting requests. Please try again in a few minutes." : "GitHub did not allow that. You may need to turn on private repositories or full access, or approve the app for the organization.", "forbidden");
        if (res.StatusCode == HttpStatusCode.NotFound) throw new AppException(404, "GitHub could not find that. If it is private, connect with private repositories.", "not_found");
        if (!res.IsSuccessStatusCode) throw new AppException(502, "GitHub could not complete the request.");
        var text = await res.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static GhItemDto ToItem(JsonElement i)
    {
        var url = Str(i, "html_url") ?? "";
        var repoUrl = Str(i, "repository_url") ?? "";
        var repo = repoUrl.Contains("/repos/") ? repoUrl[(repoUrl.IndexOf("/repos/", StringComparison.Ordinal) + 7)..] : "";
        if (repo.Length == 0 && url.StartsWith("https://github.com/", StringComparison.Ordinal)) { var parts = url["https://github.com/".Length..].Split('/'); if (parts.Length >= 2) repo = $"{parts[0]}/{parts[1]}"; }
        var number = Num(i, "number");
        var labels = i.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => Str(x, "name") ?? "").Where(x => x.Length > 0).Take(5).ToArray() : [];
        return new GhItemDto($"{repo}#{number}", repo, number, Str(i, "title") ?? "", Str(i, "state") ?? "", i.TryGetProperty("pull_request", out _), Flag(i, "draft"),
            Str(Obj(i, "user"), "login") ?? "", url, Num(i, "comments"), Date(Str(i, "updated_at")), labels);
    }

    private static string Query(string kind, string type, string state)
    {
        var parts = new List<string> { "archived:false" };
        parts.Add(kind switch { "assigned" => "assignee:@me", "created" => "author:@me", "mentioned" => "mentions:@me", _ => "review-requested:@me" });
        if (kind == "review") type = "pr"; // review requests only exist on pull requests
        if (type == "pr") parts.Add("is:pr"); else if (type == "issue") parts.Add("is:issue");
        if (state == "open") parts.Add("is:open"); else if (state == "closed") parts.Add("is:closed");
        return string.Join(' ', parts);
    }

    private static async Task<List<GhItemDto>> SearchAsync(Ctx c, string kind, string type, string state, int count, CancellationToken ct)
    {
        var url = QueryHelpers.AddQueryString("search/issues", new Dictionary<string, string?> { ["q"] = Query(kind, type, state), ["sort"] = "updated", ["order"] = "desc", ["per_page"] = Math.Clamp(count, 1, 50).ToString() });
        using var doc = await GhAsync(c, url, ct: ct);
        return doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(ToItem).ToList() : [];
    }

    private static Task<IResult> ListItemsAsync(string? kind, string? type, string? state, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        if (kind is not null && kind is not ("review" or "assigned" or "created" or "mentioned")) return Results.BadRequest("Unknown list.");
        if (type is not null && type is not ("all" or "pr" or "issue")) return Results.BadRequest("Unknown type.");
        if (state is not null && state is not ("open" or "closed" or "all")) return Results.BadRequest("Unknown state.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        return Results.Ok(await SearchAsync(c, kind ?? "review", type ?? "all", state ?? "open", 30, ct));
    });

    private static Task<IResult> ItemAsync(string owner, string repo, int number, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        if (!NamePattern.IsMatch(owner) || !NamePattern.IsMatch(repo) || number < 1) return Results.BadRequest("That item is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var issue = await GhAsync(c, $"repos/{owner}/{repo}/issues/{number}", ct: ct);
        using var comments = await GhAsync(c, $"repos/{owner}/{repo}/issues/{number}/comments?per_page=30", ct: ct);
        var item = ToItem(issue.RootElement) with { Repo = $"{owner}/{repo}" };
        var list = comments.RootElement.ValueKind == JsonValueKind.Array ? comments.RootElement.EnumerateArray().Select(x => new GhCommentDto(Str(Obj(x, "user"), "login") ?? "", Clip(Str(x, "body"), 4000), Date(Str(x, "created_at")), Str(x, "html_url"))).ToArray() : [];
        return Results.Ok(new GhItemDetailDto(item, Clip(Str(issue.RootElement, "body"), 8000), list));
    });

    private static Task<IResult> CommentAsync(string owner, string repo, int number, GhCommentRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        if (!NamePattern.IsMatch(owner) || !NamePattern.IsMatch(repo) || number < 1) return Results.BadRequest("That item is not valid.");
        var body = r.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return Results.Problem(statusCode: 400, title: "Write a comment first.");
        if (body.Length > 20000) return Results.Problem(statusCode: 400, title: "That comment is too long.");
        var c = await RequireAsync(who.UserId, true, db, protector, clients, ct);
        using var doc = await GhAsync(c, $"repos/{owner}/{repo}/issues/{number}/comments", HttpMethod.Post, new { body }, ct);
        return Results.Ok(new GhCommentDto(c.Account.AccountId, body, DateTimeOffset.UtcNow, Str(doc.RootElement, "html_url")));
    });

    private static async Task<List<GhNotificationDto>> LoadNotificationsAsync(Ctx c, bool all, int count, CancellationToken ct)
    {
        using var doc = await GhAsync(c, $"notifications?all={(all ? "true" : "false")}&per_page={Math.Clamp(count, 1, 50)}", ct: ct);
        return doc.RootElement.ValueKind != JsonValueKind.Array ? [] : doc.RootElement.EnumerateArray().Select(n =>
        {
            var subject = Obj(n, "subject");
            var apiUrl = Str(subject, "url") ?? "";
            var web = apiUrl.Replace("https://api.github.com/repos/", "https://github.com/").Replace("/pulls/", "/pull/").Replace("/commits/", "/commit/");
            return new GhNotificationDto(Str(n, "id") ?? "", Str(Obj(n, "repository"), "full_name") ?? "", Str(subject, "title") ?? "", Str(n, "reason") ?? "", Str(subject, "type") ?? "", Flag(n, "unread"), web.StartsWith("https://github.com/", StringComparison.Ordinal) ? web : null, Date(Str(n, "updated_at")));
        }).ToList();
    }

    private static Task<IResult> NotificationsAsync(bool? all, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        return Results.Ok(await LoadNotificationsAsync(c, all == true, 40, ct));
    });

    private static Task<IResult> MarkReadAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        var c = await RequireAsync(who.UserId, true, db, protector, clients, ct);
        using var _ = await GhAsync(c, "notifications", HttpMethod.Put, new { last_read_at = DateTime.UtcNow.ToString("o"), read = true }, ct);
        return Results.NoContent();
    });

    private static Task<IResult> ReposAsync(string? q, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("GitHub", async () =>
    {
        if (q is { Length: > 100 }) return Results.BadRequest("Search is too long.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var doc = await GhAsync(c, "user/repos?sort=pushed&per_page=60&affiliation=owner,collaborator,organization_member", ct: ct);
        var repos = doc.RootElement.ValueKind != JsonValueKind.Array ? [] : doc.RootElement.EnumerateArray().Select(r => new GhRepoDto(Str(r, "full_name") ?? "", Str(r, "description"), Flag(r, "private"), Str(r, "language"),
            Num(r, "stargazers_count"), Num(r, "open_issues_count"), Date(Str(r, "pushed_at")), Str(r, "html_url") ?? "")).ToList();
        if (!string.IsNullOrWhiteSpace(q)) repos = repos.Where(r => r.FullName.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return Results.Ok(repos);
    });

    // ---- dashboard and assistant ---------------------------------------------------------------

    private static string SourceTitle(string source) => source switch { "assigned" => "Assigned to me", "created" => "Created by me", "mentioned" => "Mentioning me", _ => "Review requested" };

    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is null) return Results.Ok(new AppDashboardDto(false, null, [], []));
        var s = await LoadSettingsAsync(db, Info, who.UserId, ct);
        var count = Int(s, "itemCount", 5);
        var sections = new List<DashSection>();
        var errors = new List<string>();
        try
        {
            var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
            var source = Text(s, "dashboardSource");
            var items = await SearchAsync(c, source, Bool(s, "includeIssues") ? "all" : "pr", "open", count + 10, ct);
            if (Bool(s, "hideDrafts")) items = items.Where(i => !i.Draft).ToList();
            sections.Add(new DashSection(SourceTitle(source), "/apps/github", items.Take(count).Select(i => new DashItem(i.Key, i.Title, $"{i.Repo}#{i.Number} · {i.Author}", i.Url, i.IsPr ? (i.Draft ? "Draft PR" : "PR") : "Issue")).ToArray()));
            if (Bool(s, "includeNotifications"))
            {
                var n = await LoadNotificationsAsync(c, false, count, ct);
                sections.Add(new DashSection($"Notifications{(n.Count > 0 ? $" · {n.Count} unread" : "")}", "/apps/github?tab=notifications", n.Select(x => new DashItem(x.Id, x.Title, $"{x.Repo} · {x.Reason.Replace('_', ' ')}", x.Url, null)).ToArray()));
            }
        }
        catch (AppException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add("GitHub could not be reached."); }
        return Results.Ok(new AppDashboardDto(true, account.AccountName, sections, errors));
    }

    /// <summary>Text for the chat assistant: the pull requests waiting on the owner and what they are assigned. Null when GitHub is not connected.</summary>
    internal static async Task<string?> AssistantTextAsync(string userId, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(userId, false, db, protector, clients, ct);
            var review = await SearchAsync(c, "review", "pr", "open", 8, ct);
            var assigned = await SearchAsync(c, "assigned", "all", "open", 8, ct);
            var sb = new StringBuilder();
            sb.AppendLine("GitHub — pull requests waiting for the owner's review:");
            foreach (var i in review) sb.AppendLine($"- {i.Repo}#{i.Number} {Clip(i.Title, 90)} (by {i.Author}) {i.Url}");
            if (review.Count == 0) sb.AppendLine("- none");
            sb.AppendLine("GitHub — open items assigned to the owner:");
            foreach (var i in assigned) sb.AppendLine($"- {i.Repo}#{i.Number} {Clip(i.Title, 90)} ({(i.IsPr ? "PR" : "issue")}) {i.Url}");
            if (assigned.Count == 0) sb.AppendLine("- none");
            return sb.ToString();
        }
        catch (AppException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}
