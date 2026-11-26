using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Batch 2, part 1: developer, cloud and work tools. GitLab, Bitbucket, Jira, Sentry, Docker Hub, Cloudflare, Vercel, Supabase, Firebase, n8n,
// Airtable and Confluence. Most take a token or app password that the person creates in the service (nothing to configure on the server).

public static partial class ProviderApps
{
    // ---- small helpers shared by the credential apps ---------------------------------------------

    private static InputSpec F(string key, string label, string kind = "text", string? placeholder = null, bool required = true, string? hint = null, string? def = null) =>
        new(key, label, kind, placeholder, required, null, null, def, hint);

    private static Action<HttpRequestMessage, ProviderCtx> BasicFromCred(string userKey, string secretKey) => (r, c) =>
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Cred.GetValueOrDefault(userKey)}:{c.Cred.GetValueOrDefault(secretKey)}")));

    private static Action<HttpRequestMessage, ProviderCtx> HeaderFromCred(string header, string credKey, string prefix = "") => (r, c) =>
        r.Headers.TryAddWithoutValidation(header, prefix + c.Cred.GetValueOrDefault(credKey));

    /// <summary>Tidies a web address the person typed (adds https:// when missing) and refuses link-local and metadata hosts.</summary>
    private static Func<Dictionary<string, string>, CancellationToken, Task> PrepareUrl(string key, bool allowHttp = true, bool addScheme = true) => async (v, ct) =>
    {
        var raw = v[key].Trim();
        if (addScheme && !raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        var uri = await HostGuard.CheckAsync(raw, allowHttp, ct);
        v[key] = (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');
    };

    private static object Adf(string text) => new { type = "doc", version = 1, content = new[] { new { type = "paragraph", content = text.Length == 0 ? Array.Empty<object>() : new object[] { new { type = "text", text } } } } };

    private static string Ago(JsonElement e, string path) => J.When(J.S(e, path));

    // ============================================================================================
    // GitLab (gitlab.com or your own server; a personal access token)
    // ============================================================================================
    private static string GlBase(ProviderCtx c) => c.Cred["url"].TrimEnd('/') + "/api/v4/";

    private static IReadOnlyList<AppItem> GlMergeRequests(JsonElement root) => J.Items(root, "", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "title") ?? "Merge request",
        $"{J.S(m, "references.full")} · {J.S(m, "author.username")} · {Ago(m, "updated_at")}", J.S(m, "web_url"), J.S(m, "draft") == "true" ? "draft" : J.S(m, "merge_status")));

    private static ProviderApp GitLab() => new()
    {
        Key = "gitlab", Name = "GitLab", Icon = "SiGitlab", Color = "#FC6D26", Account = "Merge requests, issues and to-dos",
        ConnectTitle = "Connect GitLab", ConnectLabel = "Connect GitLab",
        ConnectBlurb = "Works with gitlab.com and your own GitLab server. Create a personal access token (Preferences → Access tokens) and paste it with your server address.",
        WriteLabel = "Allow Daksh to comment and create issues", WriteHint = "Full access lets Daksha comment on merge requests and issues and open new issues.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#gitlab",
        Ask = Rx("gitlab|merge requests?|mrs?|pipelines?"),
        NoBearer = true, Decorate = HeaderFromCred("PRIVATE-TOKEN", "token"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** in GitLab (Preferences → Access tokens) with the `read_api` scope (add `api` to allow commenting and creating issues).", "In Daksha they enter their GitLab address (`https://gitlab.com` or their own server) and the token."],
        Notes = "Self-hosted servers must be reachable from the Daksha server.",
        Credentials = new()
        {
            Fields = [F("url", "GitLab address", "url", "https://gitlab.com", def: "https://gitlab.com"), F("token", "Personal access token", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await c.GetAsync(GlBase(c) + "user"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", "@" + (J.S(d.RootElement, "username") ?? "gitlab"), new() { ["username"] = J.S(d.RootElement, "username") ?? "" }); },
        },
        Views =
        [
            new ViewSpec { Id = "assigned", Label = "Assigned to me", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(GlBase(c) + "merge_requests?scope=assigned_to_me&state=opened&per_page=25"); return GlMergeRequests(d.RootElement); } },
            new ViewSpec { Id = "review", Label = "To review", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(GlBase(c) + $"merge_requests?reviewer_username={J.E(c.Extra("username"))}&state=opened&scope=all&per_page=25"); return GlMergeRequests(d.RootElement); } },
            new ViewSpec { Id = "issues", Label = "Issues", Load = async (c, _) => { using var d = await c.GetAsync(GlBase(c) + "issues?scope=assigned_to_me&state=opened&per_page=25");
                return J.Items(d.RootElement, "", i => new AppItem(J.S(i, "id") ?? "", J.S(i, "title") ?? "Issue", $"{J.S(i, "references.full")} · {Ago(i, "updated_at")}", J.S(i, "web_url"), J.S(i, "state"))); } },
            new ViewSpec { Id = "todos", Label = "To-dos", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(GlBase(c) + "todos?state=pending&per_page=25");
                return J.Items(d.RootElement, "", t => new AppItem(J.S(t, "id") ?? "", J.S(t, "target.title") ?? J.S(t, "body") ?? "To-do", $"{J.S(t, "action_name")?.Replace('_', ' ')} · {J.S(t, "project.path_with_namespace")}", J.S(t, "target_url"))); } },
            new ViewSpec { Id = "projects", Label = "Projects", Load = async (c, _) => { using var d = await c.GetAsync(GlBase(c) + "projects?membership=true&order_by=last_activity_at&per_page=25");
                return J.Items(d.RootElement, "", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name_with_namespace") ?? "Project", $"{Ago(p, "last_activity_at")} · {J.Clip(J.S(p, "description"), 80)}", J.S(p, "web_url"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "comment", Label = "Comment on a merge request or issue", Inputs = [F("project", "Project", placeholder: "group/project"), new("type", "On a", "select", null, true, ["merge_requests", "issues"], null, "merge_requests"), F("iid", "Number", "number", "42"), F("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["project"], @"^[\w.\-/]{1,200}$") || !NumericId.IsMatch(i["iid"])) throw new AppException(400, "Enter the project as group/project and the number as digits.");
                    using var _ = await c.PostAsync(GlBase(c) + $"projects/{J.E(i["project"])}/{i["type"]}/{i["iid"]}/notes", new { body = i["text"] }); return new ActionResult("Comment posted."); } },
            new ActionSpec { Id = "create-issue", Label = "Open an issue", Inputs = [F("project", "Project", placeholder: "group/project"), F("title", "Title"), F("description", "Description", "textarea", required: false)],
                Run = async (c, i) => { if (!Regex.IsMatch(i["project"], @"^[\w.\-/]{1,200}$")) throw new AppException(400, "Enter the project as group/project.");
                    using var d = await c.PostAsync(GlBase(c) + $"projects/{J.E(i["project"])}/issues", new { title = i["title"], description = i["description"] }); return new ActionResult("Issue opened.", J.S(d.RootElement, "web_url")); } },
        ],
    };

    // ============================================================================================
    // Bitbucket Cloud (username and app password)
    // ============================================================================================
    private const string BbApi = "https://api.bitbucket.org/2.0/";

    private static ProviderApp Bitbucket() => new()
    {
        Key = "bitbucket", Name = "Bitbucket Cloud", Icon = "SiBitbucket", Color = "#0052CC", Account = "Repositories and pull requests",
        ConnectTitle = "Connect Bitbucket", ConnectLabel = "Connect Bitbucket",
        ConnectBlurb = "Create an app password in Bitbucket (Personal settings → App passwords) and paste it with your username.",
        WriteLabel = "Allow Daksh to comment on pull requests", WriteHint = "Full access lets Daksha comment on pull requests.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#bitbucket-cloud",
        Ask = Rx("bitbucket|pull requests?|prs?"),
        NoBearer = true, Decorate = BasicFromCred("username", "password"),
        Setup = ["Nothing to set on the server. Each person creates an **app password** (Bitbucket → Personal settings → App passwords) with Account: read, Repositories: read and Pull requests: read (add write to comment).", "In Daksha they enter their Bitbucket username and the app password."],
        Credentials = new()
        {
            Fields = [F("username", "Bitbucket username"), F("password", "App password", "secret")],
            Probe = async c => { using var d = await c.GetAsync(BbApi + "user"); return new ProfileInfo(J.S(d.RootElement, "account_id") ?? c.Cred["username"], J.S(d.RootElement, "display_name") ?? c.Cred["username"]); },
        },
        Views =
        [
            new ViewSpec { Id = "pull-requests", Label = "My pull requests", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(BbApi + $"pullrequests/{J.E(c.Account.AccountId)}?state=OPEN&pagelen=25");
                return J.Items(d.RootElement, "values", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "title") ?? "Pull request", $"{J.S(p, "destination.repository.full_name")} · {J.S(p, "author.display_name")} · {Ago(p, "updated_on")}", J.S(p, "links.html.href"), J.S(p, "state"))); } },
            new ViewSpec { Id = "repositories", Label = "Repositories", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(BbApi + "repositories?role=member&sort=-updated_on&pagelen=25");
                return J.Items(d.RootElement, "values", r => new AppItem(J.S(r, "uuid") ?? "", J.S(r, "full_name") ?? "Repository", $"{Ago(r, "updated_on")} · {J.Clip(J.S(r, "description"), 80)}", J.S(r, "links.html.href"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "comment", Label = "Comment on a pull request", Inputs = [F("repo", "Repository", placeholder: "workspace/repo-slug"), F("id", "Pull request number", "number"), F("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["repo"], @"^[\w.\-]{1,100}/[\w.\-]{1,100}$") || !NumericId.IsMatch(i["id"])) throw new AppException(400, "Enter the repository as workspace/repo and the number as digits.");
                    using var _ = await c.PostAsync(BbApi + $"repositories/{i["repo"]}/pullrequests/{i["id"]}/comments", new { content = new { raw = i["text"] } }); return new ActionResult("Comment posted."); } },
        ],
    };

    // ============================================================================================
    // Jira Cloud (site, email and API token)
    // ============================================================================================
    private static string JiraBase(ProviderCtx c) => $"https://{c.Cred["site"]}/rest/api/3/";

    private static IReadOnlyList<AppItem> JiraIssues(ProviderCtx c, JsonElement root) => J.Items(root, "issues", i => new AppItem(J.S(i, "key") ?? "", $"{J.S(i, "key")} · {J.S(i, "fields.summary")}",
        $"{J.S(i, "fields.priority.name")} · {Ago(i, "fields.updated")}", $"https://{c.Cred["site"]}/browse/{J.S(i, "key")}", J.S(i, "fields.status.name")));

    private static async Task<IReadOnlyList<AppItem>> JiraSearchAsync(ProviderCtx c, string jql, int max = 25)
    {
        using var d = await c.GetAsync(JiraBase(c) + $"search/jql?jql={J.E(jql)}&fields=summary,status,priority,updated&maxResults={max}");
        return JiraIssues(c, d.RootElement);
    }

    private static ProviderApp Jira() => new()
    {
        Key = "jira", Name = "Jira", Icon = "SiJira", Color = "#0052CC", Account = "Issues and projects",
        ConnectTitle = "Connect Jira Cloud", ConnectLabel = "Connect Jira",
        ConnectBlurb = "Enter your Jira site (for example your-team.atlassian.net), your Atlassian email and an API token (id.atlassian.com → Security → API tokens).",
        WriteLabel = "Allow Daksh to create and comment on issues", WriteHint = "Full access lets Daksha create issues and add comments.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#jira",
        Ask = Rx("jira|tickets?|sprints?|backlog|issues?"),
        NoBearer = true, Decorate = BasicFromCred("email", "token"),
        Setup = ["Nothing to set on the server. Each person creates an **API token** at id.atlassian.com → Security → API tokens.", "In Daksha they enter their site (`your-team.atlassian.net`), Atlassian email and the token."],
        Notes = "Jira Server / Data Center is not covered (Jira Cloud only).",
        Credentials = new()
        {
            Fields = [F("site", "Jira site", placeholder: "your-team.atlassian.net"), F("email", "Atlassian email", "text", "you@company.com"), F("token", "API token", "secret")],
            Prepare = async (v, ct) => { var s = Regex.Replace(v["site"].Trim().ToLowerInvariant(), "^https?://", "").TrimEnd('/'); if (!Regex.IsMatch(s, @"^[a-z0-9\-]+\.atlassian\.net$")) throw new AppException(400, "Enter your site like your-team.atlassian.net."); v["site"] = s; await Task.CompletedTask; },
            Probe = async c => { using var d = await c.GetAsync(JiraBase(c) + "myself"); return new ProfileInfo(J.S(d.RootElement, "accountId") ?? c.Cred["email"], J.S(d.RootElement, "displayName") ?? c.Cred["email"], new() { ["site"] = c.Cred["site"] }); },
        },
        Views =
        [
            new ViewSpec { Id = "mine", Label = "Assigned to me", Dashboard = true, Load = (c, _) => JiraSearchAsync(c, "assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC") },
            new ViewSpec { Id = "reported", Label = "Reported by me", Load = (c, _) => JiraSearchAsync(c, "reporter = currentUser() ORDER BY created DESC") },
            new ViewSpec { Id = "recent", Label = "Updated this week", Dashboard = true, Load = (c, _) => JiraSearchAsync(c, "updated >= -7d ORDER BY updated DESC") },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search issues", placeholder: "text in the summary or description")], Load = (c, i) => JiraSearchAsync(c, $"text ~ \"{i["q"].Replace("\\", " ").Replace("\"", " ")}\" ORDER BY updated DESC", 20) },
            new ViewSpec { Id = "projects", Label = "Projects", Load = async (c, _) => { using var d = await c.GetAsync(JiraBase(c) + "project/search?maxResults=50");
                return J.Items(d.RootElement, "values", p => new AppItem(J.S(p, "key") ?? "", $"{J.S(p, "key")} · {J.S(p, "name")}", J.S(p, "projectTypeKey"), $"https://{c.Cred["site"]}/browse/{J.S(p, "key")}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create", Label = "Create an issue", Inputs = [F("project", "Project key", placeholder: "ABC"), F("summary", "Summary"), F("description", "Description", "textarea", required: false), new("type", "Type", "select", null, true, ["Task", "Bug", "Story"], null, "Task")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["project"], @"^[A-Z][A-Z0-9_]{1,15}$")) throw new AppException(400, "A project key is capital letters, like ABC.");
                    using var d = await c.PostAsync(JiraBase(c) + "issue", new { fields = new { project = new { key = i["project"] }, summary = i["summary"], issuetype = new { name = i["type"] }, description = Adf(i["description"]) } });
                    var key = J.S(d.RootElement, "key"); return new ActionResult($"Created {key}.", $"https://{c.Cred["site"]}/browse/{key}"); } },
            new ActionSpec { Id = "comment", Label = "Comment on an issue", Inputs = [F("key", "Issue key", placeholder: "ABC-123"), F("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["key"], @"^[A-Z][A-Z0-9_]{1,15}-\d{1,7}$")) throw new AppException(400, "An issue key looks like ABC-123.");
                    using var _ = await c.PostAsync(JiraBase(c) + $"issue/{i["key"]}/comment", new { body = Adf(i["text"]) }); return new ActionResult("Comment added.", $"https://{c.Cred["site"]}/browse/{i["key"]}"); } },
        ],
    };

    // ============================================================================================
    // Sentry
    // ============================================================================================
    private static string SentryBase(ProviderCtx c) => (c.Cred.GetValueOrDefault("host") is { Length: > 0 } h ? h : "https://sentry.io").TrimEnd('/') + "/api/0/";

    private static ProviderApp Sentry() => new()
    {
        Key = "sentry", Name = "Sentry", Icon = "SiSentry", Color = "#362D59", Account = "Errors and projects",
        ConnectTitle = "Connect Sentry", ConnectLabel = "Connect Sentry",
        ConnectBlurb = "Create an auth token in Sentry (Settings → Account → API → Auth Tokens) with read access to issues and projects.",
        WriteLabel = "Allow Daksh to resolve issues", WriteHint = "Full access lets Daksha mark issues resolved.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#sentry",
        Ask = Rx("sentry|errors?|crash(es)?|exceptions?"),
        Decorate = (r, c) => { },
        Setup = ["Nothing to set on the server. Each person creates a **personal auth token** in Sentry with `org:read`, `project:read` and `event:read` (add `event:write` to resolve issues).", "In Daksha they paste the token (and the address of a self-hosted Sentry, if they use one)."],
        Credentials = new()
        {
            Fields = [F("token", "Auth token", "secret"), F("host", "Sentry address", "url", "https://sentry.io", required: false, def: "https://sentry.io")],
            Prepare = async (v, ct) => { if (v["host"].Length == 0) v["host"] = "https://sentry.io"; await PrepareUrl("host")(v, ct); },
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(SentryBase(c) + "organizations/");
                var org = J.Arr(d.RootElement, "").FirstOrDefault(); if (org.ValueKind != JsonValueKind.Object) throw new AppException(400, "That token cannot see any Sentry organization.");
                return new ProfileInfo(J.S(org, "slug") ?? "", J.S(org, "name") ?? "Sentry", new() { ["org"] = J.S(org, "slug") ?? "" }); },
        },
        Views =
        [
            new ViewSpec { Id = "issues", Label = "Unresolved issues", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(SentryBase(c) + $"organizations/{J.E(c.Extra("org"))}/issues/?query={J.E("is:unresolved")}&limit=25&statsPeriod=24h");
                return J.Items(d.RootElement, "", i => new AppItem(J.S(i, "id") ?? "", J.S(i, "title") ?? "Issue", $"{J.S(i, "project.slug")} · {J.S(i, "culprit")} · {J.S(i, "count")} events, {J.S(i, "userCount")} users", J.S(i, "permalink"), J.S(i, "level"))); } },
            new ViewSpec { Id = "projects", Label = "Projects", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(SentryBase(c) + $"organizations/{J.E(c.Extra("org"))}/projects/");
                return J.Items(d.RootElement, "", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name") ?? "Project", J.S(p, "platform"), null, J.S(p, "status"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "resolve", Label = "Resolve an issue", Inputs = [F("id", "Issue id", "number", hint: "The number shown in the issue's address.")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["id"])) throw new AppException(400, "An issue id is a number."); c.Token = c.Cred["token"];
                    using var _ = await c.JsonAsync(HttpMethod.Put, SentryBase(c) + $"organizations/{J.E(c.Extra("org"))}/issues/{i["id"]}/", new { status = "resolved" }); return new ActionResult("Marked resolved."); } },
        ],
    };

    // ============================================================================================
    // Docker Hub
    // ============================================================================================
    private static async Task DockerLoginAsync(ProviderCtx c)
    {
        using var d = await c.PostAsync("https://hub.docker.com/v2/auth/token", new { identifier = c.Cred["username"], secret = c.Cred["token"] });
        c.Token = J.S(d.RootElement, "access_token") ?? throw new AppException(409, "Docker Hub did not accept the sign-in. Reconnect Docker Hub.", "reconnect_required");
    }

    private static ProviderApp DockerHub() => new()
    {
        Key = "docker-hub", Name = "Docker Hub", Icon = "SiDocker", Color = "#2496ED", Account = "Repositories and tags",
        ConnectTitle = "Connect Docker Hub", ConnectLabel = "Connect Docker Hub",
        ConnectBlurb = "Create a personal access token (Docker Hub → Account settings → Personal access tokens) and paste it with your username.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#docker-hub",
        Ask = Rx("docker|docker hub|images?|containers?"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** with Read-only permission (Docker Hub → Account settings → Personal access tokens).", "In Daksha they enter their Docker Hub username and the token."],
        Credentials = new()
        {
            Fields = [F("username", "Docker Hub username"), F("token", "Access token", "secret")],
            Probe = async c => { await DockerLoginAsync(c); return new ProfileInfo(c.Cred["username"].ToLowerInvariant(), c.Cred["username"]); },
        },
        Views =
        [
            new ViewSpec { Id = "repositories", Label = "Repositories", Dashboard = true, Load = async (c, _) => { await DockerLoginAsync(c); using var d = await c.GetAsync($"https://hub.docker.com/v2/namespaces/{J.E(c.Account.AccountId)}/repositories?page_size=25&ordering=last_updated");
                return J.Items(d.RootElement, "results", r => new AppItem(J.S(r, "name") ?? "", $"{c.Account.AccountId}/{J.S(r, "name")}", $"{J.S(r, "pull_count")} pulls · {J.S(r, "star_count")} stars · {Ago(r, "last_updated")}", $"https://hub.docker.com/r/{c.Account.AccountId}/{J.S(r, "name")}", J.S(r, "is_private") == "true" ? "private" : null, null, "tags")); } },
            new ViewSpec { Id = "tags", Label = "Tags", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^[\w.\-]{1,100}$")) throw new AppException(400, "That repository name is not valid."); await DockerLoginAsync(c);
                using var d = await c.GetAsync($"https://hub.docker.com/v2/namespaces/{J.E(c.Account.AccountId)}/repositories/{J.E(i["arg"])}/tags?page_size=25");
                return J.Items(d.RootElement, "results", t => new AppItem(J.S(t, "name") ?? "", J.S(t, "name") ?? "", $"{Ago(t, "last_updated")} · {(long.TryParse(J.S(t, "full_size"), out var b) ? $"{b / 1_048_576} MB" : "")}")); } },
        ],
    };

    // ============================================================================================
    // Cloudflare
    // ============================================================================================
    private const string CfApi = "https://api.cloudflare.com/client/v4/";
    private static readonly Regex CfId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    private static ProviderApp Cloudflare() => new()
    {
        Key = "cloudflare", Name = "Cloudflare", Icon = "SiCloudflare", Color = "#F38020", Account = "Zones and DNS records",
        ConnectTitle = "Connect Cloudflare", ConnectLabel = "Connect Cloudflare",
        ConnectBlurb = "Create an API token (My Profile → API Tokens) with Zone: Read and DNS: Read (add Edit to change DNS and purge the cache) and paste it here.",
        WriteLabel = "Allow Daksh to change DNS and purge the cache", WriteHint = "Full access lets Daksha add DNS records and purge a zone's cache.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#cloudflare",
        Ask = Rx("cloudflare|dns|zones?|domain"),
        Setup = ["Nothing to set on the server. Each person creates an **API token** (My Profile → API Tokens) scoped to Zone:Read and DNS:Read (plus DNS:Edit and Cache Purge for full access)."],
        Credentials = new()
        {
            Fields = [F("token", "API token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(CfApi + "user/tokens/verify"); return new ProfileInfo(J.S(d.RootElement, "result.id") ?? "cloudflare", "Cloudflare token (" + (J.S(d.RootElement, "result.status") ?? "active") + ")"); },
        },
        Views =
        [
            new ViewSpec { Id = "zones", Label = "Zones", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(CfApi + "zones?per_page=50");
                return J.Items(d.RootElement, "result", z => new AppItem(J.S(z, "id") ?? "", J.S(z, "name") ?? "Zone", J.S(z, "plan.name"), $"https://dash.cloudflare.com/{J.S(z, "account.id")}/{J.S(z, "name")}", J.S(z, "status"), null, "dns")); } },
            new ViewSpec { Id = "dns", Label = "DNS records", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!CfId.IsMatch(i["arg"])) throw new AppException(400, "That zone is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(CfApi + $"zones/{i["arg"]}/dns_records?per_page=100");
                return J.Items(d.RootElement, "result", r => new AppItem(J.S(r, "id") ?? "", $"{J.S(r, "type")} {J.S(r, "name")}", J.Clip(J.S(r, "content"), 80), null, J.S(r, "proxied") == "true" ? "proxied" : null), 100); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "purge", Label = "Purge a zone's cache", Inputs = [new("zone", "Zone", "select-view", null, true, null, "zones")],
                Run = async (c, i) => { if (!CfId.IsMatch(i["zone"])) throw new AppException(400, "Choose a zone."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(CfApi + $"zones/{i["zone"]}/purge_cache", new { purge_everything = true }); return new ActionResult("Cache purged."); } },
            new ActionSpec { Id = "add-dns", Label = "Add a DNS record", Inputs = [new("zone", "Zone", "select-view", null, true, null, "zones"), new("type", "Type", "select", null, true, ["A", "AAAA", "CNAME", "TXT"], null, "A"), F("name", "Name", placeholder: "www or www.example.com"), F("content", "Value", placeholder: "203.0.113.10"), new("proxied", "Proxied through Cloudflare", "select", null, false, ["false", "true"], null, "false")],
                Run = async (c, i) => { if (!CfId.IsMatch(i["zone"])) throw new AppException(400, "Choose a zone."); c.Token = c.Cred["token"];
                    using var _ = await c.PostAsync(CfApi + $"zones/{i["zone"]}/dns_records", new { type = i["type"], name = i["name"], content = i["content"], ttl = 1, proxied = i["proxied"] == "true" && i["type"] is "A" or "AAAA" or "CNAME" }); return new ActionResult("DNS record added."); } },
        ],
    };

    // ============================================================================================
    // Vercel
    // ============================================================================================
    private static ProviderApp Vercel() => new()
    {
        Key = "vercel", Name = "Vercel", Icon = "SiVercel", Color = "#000000", Account = "Projects and deployments",
        ConnectTitle = "Connect Vercel", ConnectLabel = "Connect Vercel",
        ConnectBlurb = "Create an access token (Account Settings → Tokens) and paste it here.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#vercel",
        Ask = Rx("vercel|deployments?|deploys?"),
        Setup = ["Nothing to set on the server. Each person creates an **access token** (Vercel → Account Settings → Tokens) and pastes it into Daksha."],
        Credentials = new()
        {
            Fields = [F("token", "Access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync("https://api.vercel.com/v2/user"); return new ProfileInfo(J.S(d.RootElement, "user.id") ?? "vercel", J.First(d.RootElement, "user.username", "user.email") ?? "Vercel"); },
        },
        Views =
        [
            new ViewSpec { Id = "deployments", Label = "Deployments", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync("https://api.vercel.com/v6/deployments?limit=25");
                return J.Items(d.RootElement, "deployments", x => new AppItem(J.S(x, "uid") ?? "", J.S(x, "name") ?? "Deployment", $"{J.Clip(J.S(x, "meta.githubCommitMessage"), 70)} · {J.When(J.S(x, "created")?.Length > 3 ? (long.Parse(J.S(x, "created")!) / 1000).ToString() : null)}", $"https://{J.S(x, "url")}", J.S(x, "state"))); } },
            new ViewSpec { Id = "projects", Label = "Projects", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync("https://api.vercel.com/v9/projects?limit=25");
                return J.Items(d.RootElement, "projects", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name") ?? "Project", J.S(p, "framework"), $"https://vercel.com/{J.S(p, "accountId")}/{J.S(p, "name")}")); } },
        ],
    };

    // ============================================================================================
    // Supabase (Management API)
    // ============================================================================================
    private static ProviderApp Supabase() => new()
    {
        Key = "supabase", Name = "Supabase", Icon = "SiSupabase", Color = "#3FCF8E", Account = "Projects and their status",
        ConnectTitle = "Connect Supabase", ConnectLabel = "Connect Supabase",
        ConnectBlurb = "Create a personal access token (Dashboard → Account → Access Tokens) and paste it here.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#supabase",
        Ask = Rx("supabase"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** (Supabase dashboard → Account → Access Tokens)."],
        Credentials = new()
        {
            Fields = [F("token", "Access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync("https://api.supabase.com/v1/projects"); return new ProfileInfo("supabase", $"Supabase ({J.Arr(d.RootElement, "").Count()} projects)"); },
        },
        Views =
        [
            new ViewSpec { Id = "projects", Label = "Projects", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync("https://api.supabase.com/v1/projects");
                return J.Items(d.RootElement, "", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name") ?? "Project", $"{J.S(p, "region")} · {J.When(J.S(p, "created_at"))}", $"https://supabase.com/dashboard/project/{J.S(p, "id")}", J.S(p, "status"))); } },
        ],
    };

    // ============================================================================================
    // Firebase (Google sign-in with read-only access to the Firebase Management API)
    // ============================================================================================
    private static ProviderApp Firebase() => new()
    {
        Key = "firebase", Name = "Firebase", Icon = "SiFirebase", Color = "#DD2C00", Account = "Projects and apps",
        ConnectTitle = "Connect Firebase", ConnectLabel = "Continue with Google",
        ConnectBlurb = "Sign in with the Google account that owns your Firebase projects. Daksha can list your projects and their apps.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#firebase",
        Ask = Rx("firebase"),
        Setup = ["Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Firebase Management API** and add the scope `.../auth/firebase.readonly` to the consent screen."],
        OAuth = new()
        {
            Section = "Firebase", Fallbacks = ["Gmail", "Google"], AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
            ReadScopes = ["openid", "email", "https://www.googleapis.com/auth/firebase.readonly"], AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
        },
        Profile = async c => { using var d = await c.GetAsync("https://openidconnect.googleapis.com/v1/userinfo"); return new ProfileInfo(J.S(d.RootElement, "sub") ?? "", J.S(d.RootElement, "email") ?? "Google account"); },
        Views =
        [
            new ViewSpec { Id = "projects", Label = "Projects", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://firebase.googleapis.com/v1beta1/projects?pageSize=50");
                return J.Items(d.RootElement, "results", p => new AppItem(J.S(p, "projectId") ?? "", J.S(p, "displayName") ?? J.S(p, "projectId") ?? "Project", J.S(p, "projectId"), $"https://console.firebase.google.com/project/{J.S(p, "projectId")}", J.S(p, "state"), null, "apps")); } },
            new ViewSpec { Id = "apps", Label = "Apps", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^[a-z][a-z0-9\-]{4,29}$")) throw new AppException(400, "That project is not valid.");
                using var d = await c.GetAsync($"https://firebase.googleapis.com/v1beta1/projects/{i["arg"]}:searchApps?pageSize=50");
                return J.Items(d.RootElement, "apps", a => new AppItem(J.S(a, "appId") ?? "", J.S(a, "displayName") ?? J.S(a, "appId") ?? "App", J.S(a, "appId"), null, J.S(a, "platform"))); } },
        ],
    };

    // ============================================================================================
    // n8n (your own server)
    // ============================================================================================
    private static ProviderApp N8n() => new()
    {
        Key = "n8n", Name = "n8n", Icon = "mono:n8n", Color = "#EA4B71", Account = "Workflows and executions",
        ConnectTitle = "Connect n8n", ConnectLabel = "Connect n8n",
        ConnectBlurb = "Enter your n8n address and an API key (Settings → n8n API → Create an API key).",
        WriteLabel = "Allow Daksh to turn workflows on and off", WriteHint = "Full access lets Daksha activate and deactivate workflows.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#n8n",
        Ask = Rx("n8n|workflows?|automations?"),
        NoBearer = true, Decorate = HeaderFromCred("X-N8N-API-KEY", "key"),
        Setup = ["Nothing to set on the server. Each person creates an **API key** in their n8n (Settings → n8n API) and enters it with the address of their n8n.", "The Daksha server must be able to reach that address."],
        Credentials = new()
        {
            Fields = [F("url", "n8n address", "url", "https://n8n.example.com"), F("key", "API key", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await c.GetAsync(c.Cred["url"] + "/api/v1/workflows?limit=1"); return new ProfileInfo(new Uri(c.Cred["url"]).Host, "n8n at " + new Uri(c.Cred["url"]).Host); },
        },
        Views =
        [
            new ViewSpec { Id = "workflows", Label = "Workflows", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/api/v1/workflows?limit=50");
                return J.Items(d.RootElement, "data", w => new AppItem(J.S(w, "id") ?? "", J.S(w, "name") ?? "Workflow", $"Updated {Ago(w, "updatedAt")}", null, J.S(w, "active") == "true" ? "active" : "off")); } },
            new ViewSpec { Id = "executions", Label = "Recent runs", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/api/v1/executions?limit=25");
                return J.Items(d.RootElement, "data", e => new AppItem(J.S(e, "id") ?? "", $"Workflow {J.S(e, "workflowId")} · run {J.S(e, "id")}", Ago(e, "startedAt"), null, J.S(e, "status") ?? (J.S(e, "finished") == "true" ? "success" : "running"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "toggle", Label = "Turn a workflow on or off", Inputs = [new("workflow", "Workflow", "select-view", null, true, null, "workflows"), new("state", "Set to", "select", null, true, ["activate", "deactivate"], null, "activate")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["workflow"], @"^[\w\-]{1,60}$")) throw new AppException(400, "Choose a workflow."); using var _ = await c.PostAsync(c.Cred["url"] + $"/api/v1/workflows/{J.E(i["workflow"])}/{i["state"]}"); return new ActionResult(i["state"] == "activate" ? "Workflow is on." : "Workflow is off."); } },
        ],
    };

    // ============================================================================================
    // Airtable (personal access token)
    // ============================================================================================
    private const string AtApi = "https://api.airtable.com/v0/";
    private static readonly Regex AtId = new(@"^(app|tbl|rec)[A-Za-z0-9]{10,20}$", RegexOptions.Compiled);

    private static AppItem AtRecord(JsonElement r)
    {
        var fields = J.At(r, "fields");
        var pairs = fields.ValueKind == JsonValueKind.Object ? fields.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True).Select(p => (p.Name, Value: p.Value.ToString())).ToList() : [];
        var title = pairs.FirstOrDefault().Value ?? J.S(r, "id") ?? "Record";
        return new AppItem(J.S(r, "id") ?? "", J.Clip(title, 90), J.Clip(string.Join(" · ", pairs.Skip(1).Take(4).Select(p => $"{p.Name}: {J.Clip(p.Value, 30)}")), 140));
    }

    private static ProviderApp Airtable() => new()
    {
        Key = "airtable", Name = "Airtable", Icon = "SiAirtable", Color = "#18BFFF", Account = "Bases, tables and records",
        ConnectTitle = "Connect Airtable", ConnectLabel = "Connect Airtable",
        ConnectBlurb = "Create a personal access token (airtable.com/create/tokens) with data.records:read and schema.bases:read for the bases you want to share.",
        WriteLabel = "Allow Daksh to add records", WriteHint = "Full access lets Daksha add records (the token also needs data.records:write).",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#airtable",
        Ask = Rx("airtable|bases?|spreadsheets?|records?"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** at airtable.com/create/tokens with `data.records:read` and `schema.bases:read` (add `data.records:write` for adding records) and chooses the bases to share."],
        Credentials = new()
        {
            Fields = [F("token", "Personal access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(AtApi + "meta/whoami"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "airtable", J.S(d.RootElement, "email") ?? "Airtable"); },
        },
        Views =
        [
            new ViewSpec { Id = "bases", Label = "Bases", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(AtApi + "meta/bases");
                return J.Items(d.RootElement, "bases", b => new AppItem(J.S(b, "id") ?? "", J.S(b, "name") ?? "Base", J.S(b, "permissionLevel"), $"https://airtable.com/{J.S(b, "id")}", null, null, "tables")); } },
            new ViewSpec { Id = "tables", Label = "Tables", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!AtId.IsMatch(i["arg"])) throw new AppException(400, "That base is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(AtApi + $"meta/bases/{i["arg"]}/tables");
                return J.Items(d.RootElement, "tables", t => new AppItem($"{i["arg"]}/{J.S(t, "id")}", J.S(t, "name") ?? "Table", $"{J.Arr(t, "fields").Count()} fields", $"https://airtable.com/{i["arg"]}/{J.S(t, "id")}", null, null, "records")); } },
            new ViewSpec { Id = "records", Label = "Records", Hidden = true, TakesArg = true, Load = async (c, i) => { var p = i["arg"].Split('/'); if (p.Length != 2 || !AtId.IsMatch(p[0]) || !AtId.IsMatch(p[1])) throw new AppException(400, "That table is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(AtApi + $"{p[0]}/{p[1]}?maxRecords=25"); return J.Items(d.RootElement, "records", AtRecord); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "add-record", Label = "Add a record", Hint = "Fields as JSON, for example {\"Name\": \"Alice\", \"Status\": \"Todo\"}", Inputs = [F("base", "Base id", placeholder: "appXXXXXXXXXXXXXX"), F("table", "Table id or name", placeholder: "tblXXXXXXXXXXXXXX"), F("fields", "Fields (JSON)", "textarea")],
                Run = async (c, i) => { if (!AtId.IsMatch(i["base"]) || !Regex.IsMatch(i["table"], @"^[\w \-]{1,60}$")) throw new AppException(400, "Check the base id and the table.");
                    JsonElement fields; try { fields = JsonDocument.Parse(i["fields"]).RootElement.Clone(); } catch (JsonException) { throw new AppException(400, "The fields must be valid JSON."); }
                    if (fields.ValueKind != JsonValueKind.Object) throw new AppException(400, "The fields must be a JSON object.");
                    c.Token = c.Cred["token"]; using var d = await c.PostAsync(AtApi + $"{i["base"]}/{J.E(i["table"])}", new { fields }); return new ActionResult("Record added."); } },
        ],
    };

    // ============================================================================================
    // Confluence Cloud
    // ============================================================================================
    private static ProviderApp Confluence() => new()
    {
        Key = "confluence", Name = "Confluence", Icon = "SiConfluence", Color = "#172B4D", Account = "Pages and spaces",
        ConnectTitle = "Connect Confluence Cloud", ConnectLabel = "Connect Confluence",
        ConnectBlurb = "Enter your site (your-team.atlassian.net), your Atlassian email and an API token (id.atlassian.com → Security → API tokens).",
        WriteLabel = "Allow Daksh to create pages", WriteHint = "Full access lets Daksha create pages.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#confluence",
        Ask = Rx("confluence|wiki|knowledge base|docs?"),
        NoBearer = true, Decorate = BasicFromCred("email", "token"),
        Setup = ["Nothing to set on the server. Each person creates an **API token** at id.atlassian.com → Security → API tokens and enters it with their site and email."],
        Notes = "Confluence Cloud only (not Data Center).",
        Credentials = new()
        {
            Fields = [F("site", "Confluence site", placeholder: "your-team.atlassian.net"), F("email", "Atlassian email"), F("token", "API token", "secret")],
            Prepare = async (v, ct) => { var s = Regex.Replace(v["site"].Trim().ToLowerInvariant(), "^https?://", "").TrimEnd('/'); if (!Regex.IsMatch(s, @"^[a-z0-9\-]+\.atlassian\.net$")) throw new AppException(400, "Enter your site like your-team.atlassian.net."); v["site"] = s; await Task.CompletedTask; },
            Probe = async c => { using var d = await c.GetAsync($"https://{c.Cred["site"]}/wiki/rest/api/user/current"); return new ProfileInfo(J.S(d.RootElement, "accountId") ?? c.Cred["email"], J.S(d.RootElement, "displayName") ?? c.Cred["email"]); },
        },
        Views =
        [
            new ViewSpec { Id = "recent", Label = "Recently updated", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"https://{c.Cred["site"]}/wiki/api/v2/pages?sort=-modified-date&limit=25");
                return J.Items(d.RootElement, "results", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "title") ?? "Page", Ago(p, "version.createdAt"), $"https://{c.Cred["site"]}/wiki{J.S(p, "_links.webui")}", J.S(p, "status"))); } },
            new ViewSpec { Id = "spaces", Label = "Spaces", Load = async (c, _) => { using var d = await c.GetAsync($"https://{c.Cred["site"]}/wiki/api/v2/spaces?limit=50");
                return J.Items(d.RootElement, "results", s => new AppItem(J.S(s, "id") ?? "", J.S(s, "name") ?? "Space", J.S(s, "key"), $"https://{c.Cred["site"]}/wiki{J.S(s, "_links.webui")}")); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search Confluence")], Load = async (c, i) => { using var d = await c.GetAsync($"https://{c.Cred["site"]}/wiki/rest/api/search?cql={J.E($"text ~ \"{i["q"].Replace("\\", " ").Replace("\"", " ")}\"")}&limit=15");
                return J.Items(d.RootElement, "results", r => new AppItem(J.S(r, "content.id") ?? J.S(r, "title") ?? "", J.S(r, "title") ?? "Page", J.Clip(Regex.Replace(J.S(r, "excerpt") ?? "", "<[^>]+>", ""), 120), $"https://{c.Cred["site"]}/wiki{J.S(r, "url")}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-page", Label = "Create a page", Inputs = [new("space", "Space", "select-view", null, true, null, "spaces"), F("title", "Title"), F("body", "Text", "textarea")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["space"])) throw new AppException(400, "Choose a space.");
                    using var d = await c.PostAsync($"https://{c.Cred["site"]}/wiki/api/v2/pages", new { spaceId = i["space"], status = "current", title = i["title"], body = new { representation = "storage", value = "<p>" + System.Net.WebUtility.HtmlEncode(i["body"]).Replace("\n", "</p><p>") + "</p>" } });
                    return new ActionResult("Page created.", $"https://{c.Cred["site"]}/wiki{J.S(d.RootElement, "_links.webui")}"); } },
        ],
    };
}
