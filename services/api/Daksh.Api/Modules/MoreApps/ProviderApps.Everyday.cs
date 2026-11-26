using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Daksh.Api.Modules;

// Batch 6: everyday tools. Todoist, Trello, Asana, ClickUp, Linear, Google Tasks (tasks); Strava (fitness); Hardcover and Readwise (reading);
// Plex (media); Splitwise, Wise, Zerodha Kite (money); Etsy (selling); HubSpot (CRM).

public static partial class ProviderApps
{
    /// <summary>Milliseconds since 1970 as the seconds text <see cref="J.When"/> understands.</summary>
    private static string? FromMs(string? ms) => long.TryParse(ms, out var n) && n > 0 ? (n / 1000).ToString(CultureInfo.InvariantCulture) : null;

    private static async Task<JsonDocument> GqlAsync(ProviderCtx c, string url, string query, object? variables = null)
    {
        var d = await c.PostAsync(url, new { query, variables });
        if (J.At(d.RootElement, "errors").ValueKind == JsonValueKind.Array) { var m = J.S(d.RootElement, "errors[0].message"); d.Dispose(); throw new AppException(400, $"{c.App.Name} said: {J.Clip(m, 200)}"); }
        return d;
    }

    private static string KeyId(ProviderCtx c, string field) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred[field])))[..12];

    // ============================================================================================
    // Todoist
    // ============================================================================================
    private const string TdApi = "https://api.todoist.com/api/v1/";

    private static IReadOnlyList<AppItem> TdTasks(JsonElement root) => J.Items(root, root.ValueKind == JsonValueKind.Array ? "" : "results", t => new AppItem(J.S(t, "id") ?? "", J.S(t, "content") ?? "Task",
        $"{J.First(t, "due.string", "due.date")} · {(J.S(t, "priority") is "4" ? "urgent" : J.S(t, "priority") is "3" ? "high" : "")}".Trim(' ', '·'), $"https://app.todoist.com/app/task/{J.S(t, "id")}", J.S(t, "labels[0]")));

    private static ProviderApp Todoist() => new()
    {
        Key = "todoist", Name = "Todoist", Icon = "mono:TD", Color = "#E44332", Account = "Tasks and projects",
        ConnectTitle = "Connect Todoist", ConnectLabel = "Connect Todoist", ConnectBlurb = "Paste your Todoist API token (Settings → Integrations → Developer). Daksha shows today's tasks and projects.",
        WriteLabel = "Allow Daksh to add and complete tasks", WriteHint = "Full access lets Daksha add tasks and tick them off.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#todoist",
        Ask = Rx("todoist|my tasks?|to-?dos?|due today|overdue"),
        Setup = ["Nothing to set on the server. Each person copies their **API token** from Todoist (Settings → Integrations → Developer) and pastes it into Daksha."],
        Credentials = new() { Fields = [F("token", "API token", "secret")], Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(TdApi + "projects?limit=1"); return new ProfileInfo(KeyId(c, "token"), "Todoist"); } },
        Views =
        [
            new ViewSpec { Id = "today", Label = "Today and overdue", Dashboard = true, Empty = "Nothing due. Enjoy the day.", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(TdApi + $"tasks/filter?query={J.E("today | overdue")}"); return TdTasks(d.RootElement); } },
            new ViewSpec { Id = "all", Label = "All tasks", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(TdApi + "tasks?limit=50"); return TdTasks(d.RootElement); } },
            new ViewSpec { Id = "projects", Label = "Projects", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(TdApi + "projects?limit=50");
                return J.Items(d.RootElement, d.RootElement.ValueKind == JsonValueKind.Array ? "" : "results", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name") ?? "Project", null, $"https://app.todoist.com/app/project/{J.S(p, "id")}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "add", Label = "Add a task", Inputs = [F("content", "Task"), F("due", "Due", placeholder: "tomorrow at 5pm", required: false)],
                Run = async (c, i) => { c.Token = c.Cred["token"]; using var d = await c.PostAsync(TdApi + "tasks", new { content = i["content"], due_string = i["due"].Length > 0 ? i["due"] : null }); return new ActionResult("Task added.", $"https://app.todoist.com/app/task/{J.S(d.RootElement, "id")}"); } },
            new ActionSpec { Id = "complete", Label = "Complete a task", Inputs = [new("task", "Task", "select-view", null, true, null, "all")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["task"], @"^[\w\-]{4,40}$")) throw new AppException(400, "Choose a task."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(TdApi + $"tasks/{i["task"]}/close"); return new ActionResult("Done."); } },
        ],
    };

    // ============================================================================================
    // Trello
    // ============================================================================================
    private const string TrApi = "https://api.trello.com/1/";
    private static readonly Regex TrId = new("^[0-9a-f]{24}$", RegexOptions.Compiled);

    private static ProviderApp Trello() => new()
    {
        Key = "trello", Name = "Trello", Icon = "mono:TR", Color = "#0079BF", Account = "Boards, lists and cards",
        ConnectTitle = "Connect Trello", ConnectLabel = "Connect Trello", ConnectBlurb = "Enter your Trello API key and token (trello.com/power-ups/admin → API key → generate a token).",
        WriteLabel = "Allow Daksh to create cards", WriteHint = "Full access lets Daksha create cards (the token needs write access).",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#trello",
        Ask = Rx("trello|boards?|cards?|kanban"),
        NoBearer = true, Decorate = (r, c) => r.Headers.TryAddWithoutValidation("Authorization", $"OAuth oauth_consumer_key=\"{c.Cred.GetValueOrDefault("key")}\", oauth_token=\"{c.Cred.GetValueOrDefault("token")}\""),
        Setup = ["Nothing to set on the server. Each person creates a Power-Up at trello.com/power-ups/admin to get an **API key**, then generates a **token** from it (read and write) and enters both in Daksha."],
        Credentials = new() { Fields = [F("key", "API key"), F("token", "Token", "secret")], Probe = async c => { using var d = await c.GetAsync(TrApi + "members/me?fields=id,fullName"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.S(d.RootElement, "fullName") ?? "Trello"); } },
        Views =
        [
            new ViewSpec { Id = "mine", Label = "My cards", Dashboard = true, Empty = "No cards are assigned to you.", Load = async (c, _) => { using var d = await c.GetAsync(TrApi + "members/me/cards?fields=name,due,url,closed");
                return J.Items(d.RootElement, "", x => J.S(x, "closed") == "true" ? null : new AppItem(J.S(x, "id") ?? "", J.S(x, "name") ?? "Card", J.S(x, "due") is { Length: > 0 } due ? "Due " + J.When(due) : null, J.S(x, "url")), 50); } },
            new ViewSpec { Id = "boards", Label = "Boards", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(TrApi + "members/me/boards?filter=open&fields=name,url,dateLastActivity");
                return J.Items(d.RootElement, "", b => new AppItem(J.S(b, "id") ?? "", J.S(b, "name") ?? "Board", $"Active {J.When(J.S(b, "dateLastActivity"))}", J.S(b, "url"), null, null, "cards")); } },
            new ViewSpec { Id = "cards", Label = "Cards", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!TrId.IsMatch(i["arg"])) throw new AppException(400, "That board is not valid."); using var d = await c.GetAsync(TrApi + $"boards/{i["arg"]}/cards?fields=name,due,url,idList&limit=100");
                return J.Items(d.RootElement, "", x => new AppItem(J.S(x, "id") ?? "", J.S(x, "name") ?? "Card", J.S(x, "due") is { Length: > 0 } due ? "Due " + J.When(due) : null, J.S(x, "url")), 100); } },
            new ViewSpec { Id = "lists", Label = "Lists", Load = async (c, _) => { using var b = await c.GetAsync(TrApi + "members/me/boards?filter=open&fields=name"); var items = new List<AppItem>();
                foreach (var board in J.Arr(b.RootElement, "").Take(10)) { using var l = await c.GetAsync(TrApi + $"boards/{J.S(board, "id")}/lists?fields=name"); items.AddRange(J.Arr(l.RootElement, "").Select(x => new AppItem(J.S(x, "id") ?? "", $"{J.S(board, "name")} / {J.S(x, "name")}"))); } return items; } },
        ],
        Actions =
        [
            new ActionSpec { Id = "card", Label = "Create a card", Inputs = [new("list", "List", "select-view", null, true, null, "lists"), F("name", "Card title"), F("desc", "Description", "textarea", required: false)],
                Run = async (c, i) => { if (!TrId.IsMatch(i["list"])) throw new AppException(400, "Choose a list."); using var d = await c.PostAsync(TrApi + "cards", new { idList = i["list"], name = i["name"], desc = i["desc"] }); return new ActionResult("Card created.", J.S(d.RootElement, "url")); } },
        ],
    };

    // ============================================================================================
    // Asana (personal access token)
    // ============================================================================================
    private const string AsApi = "https://app.asana.com/api/1.0/";

    private static ProviderApp Asana() => new()
    {
        Key = "asana", Name = "Asana", Icon = "mono:AS", Color = "#F06A6A", Account = "My tasks and projects",
        ConnectTitle = "Connect Asana", ConnectLabel = "Connect Asana", ConnectBlurb = "Create a personal access token (My settings → Apps → Developer apps) and paste it here.",
        WriteLabel = "Allow Daksh to create and complete tasks", WriteHint = "Full access lets Daksha create tasks and mark them complete.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#asana",
        Ask = Rx("asana|my tasks?|projects?"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** in Asana (My settings → Apps → Developer apps → Personal access tokens)."],
        Credentials = new()
        {
            Fields = [F("token", "Personal access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(AsApi + "users/me"); var ws = J.S(d.RootElement, "data.workspaces[0].gid") ?? throw new AppException(400, "That Asana account has no workspace.");
                return new ProfileInfo(J.S(d.RootElement, "data.gid") ?? "", J.S(d.RootElement, "data.name") ?? "Asana", new() { ["workspace"] = ws }); },
        },
        Views =
        [
            new ViewSpec { Id = "mine", Label = "My tasks", Dashboard = true, Empty = "You have no open tasks.", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(AsApi + $"tasks?assignee=me&workspace={J.E(c.Extra("workspace"))}&completed_since=now&opt_fields=name,due_on,permalink_url,projects.name&limit=50");
                return J.Items(d.RootElement, "data", t => new AppItem(J.S(t, "gid") ?? "", J.S(t, "name") ?? "Task", $"{(J.S(t, "due_on") is { Length: > 0 } due ? "Due " + due : "No due date")} · {J.S(t, "projects[0].name")}".Trim(' ', '·'), J.S(t, "permalink_url")), 50); } },
            new ViewSpec { Id = "projects", Label = "Projects", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(AsApi + $"projects?workspace={J.E(c.Extra("workspace"))}&archived=false&opt_fields=name,permalink_url&limit=50");
                return J.Items(d.RootElement, "data", p => new AppItem(J.S(p, "gid") ?? "", J.S(p, "name") ?? "Project", null, J.S(p, "permalink_url"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create", Label = "Create a task", Inputs = [F("name", "Task"), F("notes", "Notes", "textarea", required: false), F("due", "Due date", "date", required: false)],
                Run = async (c, i) => { c.Token = c.Cred["token"]; using var d = await c.PostAsync(AsApi + "tasks", new { data = new { name = i["name"], notes = i["notes"], assignee = "me", workspace = c.Extra("workspace"), due_on = i["due"].Length > 0 ? i["due"] : null } }); return new ActionResult("Task created.", J.S(d.RootElement, "data.permalink_url")); } },
            new ActionSpec { Id = "complete", Label = "Complete a task", Inputs = [new("task", "Task", "select-view", null, true, null, "mine")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["task"])) throw new AppException(400, "Choose a task."); c.Token = c.Cred["token"]; using var _ = await c.JsonAsync(HttpMethod.Put, AsApi + $"tasks/{i["task"]}", new { data = new { completed = true } }); return new ActionResult("Done."); } },
        ],
    };

    // ============================================================================================
    // ClickUp (personal API token, sent as is)
    // ============================================================================================
    private const string CuApi = "https://api.clickup.com/api/v2/";

    private static ProviderApp ClickUp() => new()
    {
        Key = "clickup", Name = "ClickUp", Icon = "mono:CU", Color = "#7B68EE", Account = "Tasks assigned to you",
        ConnectTitle = "Connect ClickUp", ConnectLabel = "Connect ClickUp", ConnectBlurb = "Copy your personal API token (Settings → Apps → API Token) and paste it here.",
        WriteLabel = "Allow Daksh to create tasks and comment", WriteHint = "Full access lets Daksha create tasks and add comments.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#clickup",
        Ask = Rx("clickup|tasks?"),
        NoBearer = true, Decorate = HeaderFromCred("Authorization", "token"),
        Setup = ["Nothing to set on the server. Each person copies their **personal API token** from ClickUp (Settings → Apps → API Token)."],
        Credentials = new()
        {
            Fields = [F("token", "Personal API token", "secret", placeholder: "pk_…")],
            Probe = async c => { using var u = await c.GetAsync(CuApi + "user"); using var t = await c.GetAsync(CuApi + "team"); var team = J.S(t.RootElement, "teams[0].id") ?? throw new AppException(400, "That token has no ClickUp workspace.");
                return new ProfileInfo(J.S(u.RootElement, "user.id") ?? "", J.S(u.RootElement, "user.username") ?? "ClickUp", new() { ["team"] = team }); },
        },
        Views =
        [
            new ViewSpec { Id = "mine", Label = "My tasks", Dashboard = true, Empty = "No open tasks are assigned to you.", Load = async (c, _) => { using var d = await c.GetAsync(CuApi + $"team/{J.E(c.Extra("team"))}/task?assignees%5B%5D={J.E(c.Account.AccountId)}&include_closed=false&order_by=due_date&page=0");
                return J.Items(d.RootElement, "tasks", t => new AppItem(J.S(t, "id") ?? "", J.S(t, "name") ?? "Task", $"{J.S(t, "list.name")} · {(FromMs(J.S(t, "due_date")) is { } due ? "Due " + J.When(due) : "no due date")}", J.S(t, "url"), J.S(t, "status.status")), 50); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create", Label = "Create a task", Inputs = [F("list", "List id", "number", hint: "The number at the end of a list's address in ClickUp."), F("name", "Task"), F("description", "Description", "textarea", required: false)],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["list"])) throw new AppException(400, "A list id is a number."); using var d = await c.PostAsync(CuApi + $"list/{i["list"]}/task", new { name = i["name"], description = i["description"] }); return new ActionResult("Task created.", J.S(d.RootElement, "url")); } },
            new ActionSpec { Id = "comment", Label = "Comment on a task", Inputs = [new("task", "Task", "select-view", null, true, null, "mine"), F("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["task"], @"^[\w]{4,20}$")) throw new AppException(400, "Choose a task."); using var _ = await c.PostAsync(CuApi + $"task/{i["task"]}/comment", new { comment_text = i["text"] }); return new ActionResult("Comment added."); } },
        ],
    };

    // ============================================================================================
    // Linear (personal API key)
    // ============================================================================================
    private const string LinearApi = "https://api.linear.app/graphql";
    private const string LinearFields = "identifier title url priority updatedAt state { name } team { key }";

    private static IReadOnlyList<AppItem> LinearIssues(JsonElement nodes) => J.Items(nodes, "", i => new AppItem(J.S(i, "identifier") ?? "", $"{J.S(i, "identifier")} · {J.S(i, "title")}", $"{J.S(i, "team.key")} · {J.When(J.S(i, "updatedAt"))}", J.S(i, "url"), J.S(i, "state.name")), 50);

    private static ProviderApp Linear() => new()
    {
        Key = "linear", Name = "Linear", Icon = "mono:LN", Color = "#5E6AD2", Account = "Issues assigned to you",
        ConnectTitle = "Connect Linear", ConnectLabel = "Connect Linear", ConnectBlurb = "Create a personal API key (Settings → Account → Security & access) and paste it here.",
        WriteLabel = "Allow Daksh to create issues and comment", WriteHint = "Full access lets Daksha create issues and add comments.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#linear",
        Ask = Rx("linear|issues?|tickets?|sprint"),
        NoBearer = true, Decorate = HeaderFromCred("Authorization", "key"),
        Setup = ["Nothing to set on the server. Each person creates a **personal API key** in Linear (Settings → Account → Security & access)."],
        Credentials = new() { Fields = [F("key", "API key", "secret", placeholder: "lin_api_…")], Probe = async c => { using var d = await GqlAsync(c, LinearApi, "{ viewer { id name email } }"); return new ProfileInfo(J.S(d.RootElement, "data.viewer.id") ?? "", J.First(d.RootElement, "data.viewer.name", "data.viewer.email") ?? "Linear"); } },
        Views =
        [
            new ViewSpec { Id = "assigned", Label = "Assigned to me", Dashboard = true, Empty = "Nothing is assigned to you.", Load = async (c, _) => { using var d = await GqlAsync(c, LinearApi, $"{{ viewer {{ assignedIssues(first: 25, orderBy: updatedAt, filter: {{ state: {{ type: {{ nin: [\"completed\", \"canceled\"] }} }} }}) {{ nodes {{ {LinearFields} }} }} }} }}"); return LinearIssues(J.At(d.RootElement, "data.viewer.assignedIssues.nodes")); } },
            new ViewSpec { Id = "created", Label = "Created by me", Load = async (c, _) => { using var d = await GqlAsync(c, LinearApi, $"{{ viewer {{ createdIssues(first: 25, orderBy: updatedAt) {{ nodes {{ {LinearFields} }} }} }} }}"); return LinearIssues(J.At(d.RootElement, "data.viewer.createdIssues.nodes")); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search issues")], Load = async (c, i) => { using var d = await GqlAsync(c, LinearApi, $"query($q: String!) {{ issues(first: 20, filter: {{ title: {{ containsIgnoreCase: $q }} }}) {{ nodes {{ {LinearFields} }} }} }}", new { q = i["q"] }); return LinearIssues(J.At(d.RootElement, "data.issues.nodes")); } },
            new ViewSpec { Id = "teams", Label = "Teams", Load = async (c, _) => { using var d = await GqlAsync(c, LinearApi, "{ teams(first: 50) { nodes { id name key } } }"); return J.Items(d.RootElement, "data.teams.nodes", t => new AppItem(J.S(t, "id") ?? "", $"{J.S(t, "name")} ({J.S(t, "key")})")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create", Label = "Create an issue", Inputs = [new("team", "Team", "select-view", null, true, null, "teams"), F("title", "Title"), F("description", "Description", "textarea", required: false)],
                Run = async (c, i) => { if (!Guid36.IsMatch(i["team"])) throw new AppException(400, "Choose a team."); using var d = await GqlAsync(c, LinearApi, "mutation($team: String!, $title: String!, $desc: String) { issueCreate(input: { teamId: $team, title: $title, description: $desc }) { success issue { identifier url } } }", new { team = i["team"], title = i["title"], desc = i["description"] });
                    return new ActionResult($"Created {J.S(d.RootElement, "data.issueCreate.issue.identifier")}.", J.S(d.RootElement, "data.issueCreate.issue.url")); } },
            new ActionSpec { Id = "comment", Label = "Comment on an issue", Inputs = [F("issue", "Issue", placeholder: "ENG-123"), F("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["issue"], @"^[A-Z][A-Z0-9]{0,9}-\d{1,7}$")) throw new AppException(400, "An issue looks like ENG-123.");
                    using var find = await GqlAsync(c, LinearApi, "query($id: String!) { issue(id: $id) { id url } }", new { id = i["issue"] }); var id = J.S(find.RootElement, "data.issue.id") ?? throw new AppException(404, "That issue was not found.");
                    using var _ = await GqlAsync(c, LinearApi, "mutation($id: String!, $body: String!) { commentCreate(input: { issueId: $id, body: $body }) { success } }", new { id, body = i["text"] }); return new ActionResult("Comment added.", J.S(find.RootElement, "data.issue.url")); } },
        ],
    };

    // ============================================================================================
    // Google Tasks (replaces Google Photos, whose API no longer lets apps read a person's own library)
    // ============================================================================================
    private const string GTasks = "https://tasks.googleapis.com/tasks/v1/";
    private static readonly Regex GtId = new(@"^[\w\-]{4,80}$", RegexOptions.Compiled);

    private static ProviderApp GoogleTasks() => new()
    {
        Key = "google-tasks", Name = "Google Tasks", Icon = "mono:GT", Color = "#4285F4", Account = "Task lists and tasks",
        ConnectTitle = "Connect Google Tasks", ConnectLabel = "Continue with Google", ConnectBlurb = "Sign in with Google and Daksha shows your task lists and open tasks.",
        WriteLabel = "Allow Daksh to add and complete tasks", WriteHint = "Full access lets Daksha add tasks and mark them done.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#google-tasks",
        Ask = Rx("google tasks|my tasks?|to-?dos?"),
        Setup = ["Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Google Tasks API** and add the scopes `tasks.readonly` and `tasks` to the consent screen."],
        Notes = "Google Photos is not offered: since 2025 Google only lets apps read photos they created themselves, not a person's own library.",
        OAuth = new()
        {
            Section = "GoogleTasks", Fallbacks = ["Gmail", "Google"], AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
            ReadScopes = ["openid", "email", "https://www.googleapis.com/auth/tasks.readonly"], WriteScopes = ["https://www.googleapis.com/auth/tasks"], AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
        },
        Profile = async c => { using var d = await c.GetAsync("https://openidconnect.googleapis.com/v1/userinfo"); return new ProfileInfo(J.S(d.RootElement, "sub") ?? "", J.S(d.RootElement, "email") ?? "Google account"); },
        Views =
        [
            new ViewSpec { Id = "open", Label = "Open tasks", Dashboard = true, Empty = "No open tasks.", Load = async (c, _) => { using var lists = await c.GetAsync(GTasks + "users/@me/lists?maxResults=10"); var items = new List<AppItem>();
                foreach (var l in J.Arr(lists.RootElement, "items").Take(8)) { using var t = await c.GetAsync(GTasks + $"lists/{J.E(J.S(l, "id"))}/tasks?showCompleted=false&maxResults=25");
                    items.AddRange(J.Arr(t.RootElement, "items").Where(x => J.S(x, "title") is { Length: > 0 }).Select(x => new AppItem($"{J.S(l, "id")}/{J.S(x, "id")}", J.S(x, "title")!, $"{J.S(l, "title")}{(J.S(x, "due") is { Length: > 0 } due ? " · due " + J.When(due) : "")}", J.S(x, "webViewLink")))); }
                return items; } },
            new ViewSpec { Id = "lists", Label = "Lists", Load = async (c, _) => { using var d = await c.GetAsync(GTasks + "users/@me/lists?maxResults=50"); return J.Items(d.RootElement, "items", l => new AppItem(J.S(l, "id") ?? "", J.S(l, "title") ?? "List", J.When(J.S(l, "updated")))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "add", Label = "Add a task", Inputs = [new("list", "List", "select-view", null, true, null, "lists"), F("title", "Task"), F("notes", "Notes", "textarea", required: false), F("due", "Due date", "date", required: false)],
                Run = async (c, i) => { if (!GtId.IsMatch(i["list"])) throw new AppException(400, "Choose a list."); using var _ = await c.PostAsync(GTasks + $"lists/{J.E(i["list"])}/tasks", new { title = i["title"], notes = i["notes"], due = i["due"].Length > 0 ? i["due"] + "T00:00:00.000Z" : null }); return new ActionResult("Task added."); } },
            new ActionSpec { Id = "complete", Label = "Complete a task", Inputs = [new("task", "Task", "select-view", null, true, null, "open")],
                Run = async (c, i) => { var p = i["task"].Split('/'); if (p.Length != 2 || !GtId.IsMatch(p[0]) || !GtId.IsMatch(p[1])) throw new AppException(400, "Choose a task."); using var _ = await c.JsonAsync(HttpMethod.Patch, GTasks + $"lists/{J.E(p[0])}/tasks/{J.E(p[1])}", new { status = "completed" }); return new ActionResult("Done."); } },
        ],
    };

    // ============================================================================================
    // Strava (read-only)
    // ============================================================================================
    private const string StravaApi = "https://www.strava.com/api/v3/";

    private static ProviderApp Strava() => new()
    {
        Key = "strava", Name = "Strava", Icon = "mono:ST", Color = "#FC4C02", Account = "Runs, rides and weekly totals",
        ConnectTitle = "Connect Strava", ConnectLabel = "Continue with Strava", ConnectBlurb = "Sign in with Strava and Daksha shows your recent activities and totals. Read-only.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#strava",
        Ask = Rx("strava|runs?|running|rides?|cycling|workouts?|activities"),
        Setup = ["strava.com/settings/api → create an app. Set **Authorization Callback Domain** to your API's domain. Copy the Client ID and Client Secret."],
        Notes = "Strava limits each app to 100 requests per 15 minutes and 1,000 per day.",
        OAuth = new() { Section = "Strava", AuthUrl = "https://www.strava.com/oauth/authorize", TokenUrl = "https://www.strava.com/oauth/token", ScopeSeparator = ",", ReadScopes = ["read", "activity:read"], AuthExtra = new() { ["approval_prompt"] = "auto" } },
        Profile = async c => { using var d = await c.GetAsync(StravaApi + "athlete"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.T(d.RootElement, "{firstname} {lastname}") is { Length: > 0 } n ? n : "Strava athlete"); },
        Views =
        [
            new ViewSpec { Id = "activities", Label = "Recent activities", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(StravaApi + "athlete/activities?per_page=25");
                return J.Items(d.RootElement, "", a => new AppItem(J.S(a, "id") ?? "", J.S(a, "name") ?? "Activity", $"{J.S(a, "type")} · {(double.TryParse(J.S(a, "distance"), CultureInfo.InvariantCulture, out var m) ? (m / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km" : "")} · {(int.TryParse(J.S(a, "moving_time"), out var s) ? $"{s / 3600}h {s % 3600 / 60}m" : "")} · {J.When(J.S(a, "start_date"))}", $"https://www.strava.com/activities/{J.S(a, "id")}")); } },
            new ViewSpec { Id = "totals", Label = "Totals", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(StravaApi + $"athletes/{J.E(c.Account.AccountId)}/stats");
                AppItem T(string label, string path) => new(path, label, $"{J.S(d.RootElement, path + ".count") ?? "0"} activities · {(double.TryParse(J.S(d.RootElement, path + ".elapsed_time"), CultureInfo.InvariantCulture, out var s) ? (int)(s / 3600) : 0)} h", null, $"{(double.TryParse(J.S(d.RootElement, path + ".distance"), CultureInfo.InvariantCulture, out var m) ? (m / 1000).ToString("0", CultureInfo.InvariantCulture) : "0")} km");
                return [T("Runs this year", "ytd_run_totals"), T("Rides this year", "ytd_ride_totals"), T("Runs, last 4 weeks", "recent_run_totals"), T("Rides, last 4 weeks", "recent_ride_totals")]; } },
        ],
    };

    // ============================================================================================
    // Hardcover (your reading list)
    // ============================================================================================
    private const string HcApi = "https://api.hardcover.app/v1/graphql";

    private static async Task<IReadOnlyList<AppItem>> HcBooksAsync(ProviderCtx c, int status, int limit = 25)
    {
        using var d = await GqlAsync(c, HcApi, $"{{ me {{ user_books(where: {{ status_id: {{ _eq: {status} }} }}, order_by: {{ updated_at: desc }}, limit: {limit}) {{ rating book {{ title slug contributions {{ author {{ name }} }} }} }} }} }}");
        var me = J.At(d.RootElement, "data.me"); if (me.ValueKind == JsonValueKind.Array && me.GetArrayLength() > 0) me = me[0];
        return J.Items(me, "user_books", b => new AppItem(J.S(b, "book.slug") ?? J.S(b, "book.title") ?? "", J.S(b, "book.title") ?? "Book", $"{string.Join(", ", J.Arr(b, "book.contributions").Select(x => J.S(x, "author.name")).Where(n => n is not null).Take(2))}{(J.S(b, "rating") is { Length: > 0 } r ? " · " + r + "★" : "")}", $"https://hardcover.app/books/{J.S(b, "book.slug")}"));
    }

    private static ProviderApp Hardcover() => new()
    {
        Key = "hardcover", Name = "Hardcover", Icon = "mono:HC", Color = "#1B1B1B", Account = "Your reading list",
        ConnectTitle = "Connect Hardcover", ConnectLabel = "Connect Hardcover", ConnectBlurb = "Copy your API token from hardcover.app/account/api and paste it here.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#hardcover",
        Ask = Rx("hardcover|books?|reading|my library"),
        NoBearer = true, Decorate = (r, c) => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Regex.Replace(c.Cred.GetValueOrDefault("token") ?? "", "^Bearer\\s+", "", RegexOptions.IgnoreCase)),
        Setup = ["Nothing to set on the server. Each person copies their **API token** from hardcover.app/account/api."],
        Notes = "Tokens expire after a year; reconnect with a new one.",
        Credentials = new() { Fields = [F("token", "API token", "secret")], Probe = async c => { using var d = await GqlAsync(c, HcApi, "{ me { id username } }"); var me = J.At(d.RootElement, "data.me"); if (me.ValueKind == JsonValueKind.Array && me.GetArrayLength() > 0) me = me[0];
            return new ProfileInfo(J.S(me, "id") ?? KeyId(c, "token"), "@" + (J.S(me, "username") ?? "reader")); } },
        Views =
        [
            new ViewSpec { Id = "reading", Label = "Currently reading", Dashboard = true, Empty = "You are not reading anything right now.", Load = (c, _) => HcBooksAsync(c, 2) },
            new ViewSpec { Id = "want", Label = "Want to read", Load = (c, _) => HcBooksAsync(c, 1) },
            new ViewSpec { Id = "read", Label = "Read", Load = (c, _) => HcBooksAsync(c, 3) },
        ],
    };

    // ============================================================================================
    // Readwise (highlights)
    // ============================================================================================
    private const string RwApi = "https://readwise.io/api/v2/";

    private static ProviderApp Readwise() => new()
    {
        Key = "readwise", Name = "Readwise", Icon = "mono:RW", Color = "#F5C400", Account = "Your highlights",
        ConnectTitle = "Connect Readwise", ConnectLabel = "Connect Readwise", ConnectBlurb = "Copy your access token from readwise.io/access_token and paste it here.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#readwise",
        Ask = Rx("readwise|highlights?|daily review"),
        NoBearer = true, Decorate = HeaderFromCred("Authorization", "token", "Token "),
        Setup = ["Nothing to set on the server. Each person copies their **access token** from readwise.io/access_token."],
        Credentials = new() { Fields = [F("token", "Access token", "secret")], Probe = async c => { using var d = await c.GetAsync(RwApi + "auth/"); return new ProfileInfo(KeyId(c, "token"), "Readwise"); } },
        Views =
        [
            new ViewSpec { Id = "review", Label = "Daily review", Dashboard = true, Empty = "No highlights to review today.", Load = async (c, _) => { using var d = await c.GetAsync(RwApi + "review/");
                return J.Items(d.RootElement, "highlights", h => new AppItem(J.S(h, "id") ?? "", J.Clip(J.S(h, "text"), 200), $"{J.S(h, "title")}{(J.S(h, "author") is { Length: > 0 } a ? " · " + a : "")}", J.S(h, "url"), null, null, null, J.S(h, "text"))); } },
            new ViewSpec { Id = "highlights", Label = "Recent highlights", Load = async (c, _) => { using var d = await c.GetAsync(RwApi + "highlights/?page_size=25");
                return J.Items(d.RootElement, "results", h => new AppItem(J.S(h, "id") ?? "", J.Clip(J.S(h, "text"), 200), J.When(J.S(h, "highlighted_at")), J.S(h, "url"), null, null, null, J.S(h, "text"))); } },
            new ViewSpec { Id = "books", Label = "Sources", Load = async (c, _) => { using var d = await c.GetAsync(RwApi + "books/?page_size=25");
                return J.Items(d.RootElement, "results", b => new AppItem(J.S(b, "id") ?? "", J.S(b, "title") ?? "Source", $"{J.S(b, "author")} · {J.S(b, "num_highlights")} highlights", J.S(b, "source_url"), J.S(b, "category"))); } },
        ],
    };

    // ============================================================================================
    // Plex (your own server)
    // ============================================================================================
    private static ProviderApp Plex() => new()
    {
        Key = "plex", Name = "Plex", Icon = "mono:PX", Color = "#E5A00D", Account = "What is playing and recently added",
        ConnectTitle = "Connect Plex", ConnectLabel = "Connect Plex", ConnectBlurb = "Enter the address of your Plex server and your Plex token. Daksha shows what is playing and what was added recently.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#plex",
        Ask = Rx("plex|now playing|recently added|movies?|tv shows?"),
        NoBearer = true, Decorate = (r, c) => { r.Headers.TryAddWithoutValidation("X-Plex-Token", c.Cred.GetValueOrDefault("token")); r.Headers.Accept.ParseAdd("application/json"); },
        Setup = ["Nothing to set on the server. Each person enters their Plex server address (`http://192.168.1.10:32400`) and a **Plex token** (in Plex Web, open any item → Get Info → View XML; the token is in the address). The Daksha server must be able to reach the Plex server."],
        Credentials = new()
        {
            Fields = [F("url", "Plex server address", "url", "http://192.168.1.10:32400"), F("token", "Plex token", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await c.GetAsync(c.Cred["url"] + "/library/sections"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"Plex at {new Uri(c.Cred["url"]).Host}"); },
        },
        Views =
        [
            new ViewSpec { Id = "playing", Label = "Now playing", Dashboard = true, Empty = "Nothing is playing.", Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/status/sessions");
                return J.Items(d.RootElement, "MediaContainer.Metadata", m => new AppItem(J.S(m, "ratingKey") ?? "", J.First(m, "grandparentTitle", "title") is { } t ? $"{t}{(J.S(m, "grandparentTitle") is not null ? " · " + J.S(m, "title") : "")}" : "Media", $"{J.S(m, "User.title")} on {J.S(m, "Player.title")}", null, J.S(m, "Player.state"))); } },
            new ViewSpec { Id = "recent", Label = "Recently added", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/library/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size=25");
                return J.Items(d.RootElement, "MediaContainer.Metadata", m => new AppItem(J.S(m, "ratingKey") ?? "", J.First(m, "grandparentTitle", "title") ?? "Media", $"{J.S(m, "type")} · {J.S(m, "year")} · added {J.When(J.S(m, "addedAt"))}")); } },
            new ViewSpec { Id = "libraries", Label = "Libraries", Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/library/sections");
                return J.Items(d.RootElement, "MediaContainer.Directory", l => new AppItem(J.S(l, "key") ?? "", J.S(l, "title") ?? "Library", J.S(l, "type"))); } },
        ],
    };

    // ============================================================================================
    // Splitwise
    // ============================================================================================
    private const string SwApi = "https://secure.splitwise.com/api/v3.0/";

    private static ProviderApp Splitwise() => new()
    {
        Key = "splitwise", Name = "Splitwise", Icon = "mono:SW", Color = "#5BC5A7", Account = "Shared expenses and balances",
        ConnectTitle = "Connect Splitwise", ConnectLabel = "Continue with Splitwise", ConnectBlurb = "Sign in with Splitwise and Daksha shows who owes what, your groups and recent expenses.",
        WriteLabel = "Allow Daksh to add expenses", WriteHint = "Splitwise has one permission for looking and changing, so this is Daksha's own switch: it only adds expenses when it is on.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#splitwise",
        Ask = Rx("splitwise|who owes|owes me|i owe|shared expenses?|split"),
        Setup = ["secure.splitwise.com/apps → **Register your application**. Set the callback URL to `<API address>/api/integrations/splitwise/callback`. Copy the consumer key (client id) and secret."],
        OAuth = new() { Section = "Splitwise", AuthUrl = "https://secure.splitwise.com/oauth/authorize", TokenUrl = "https://secure.splitwise.com/oauth/token" },
        Profile = async c => { using var d = await c.GetAsync(SwApi + "get_current_user"); return new ProfileInfo(J.S(d.RootElement, "user.id") ?? "", J.T(d.RootElement, "{user.first_name} {user.last_name}") is { Length: > 0 } n ? n : "Splitwise"); },
        Views =
        [
            new ViewSpec { Id = "friends", Label = "Balances", Dashboard = true, Empty = "You are all settled up.", Load = async (c, _) => { using var d = await c.GetAsync(SwApi + "get_friends");
                return J.Items(d.RootElement, "friends", f => J.Arr(f, "balance").FirstOrDefault() is { ValueKind: JsonValueKind.Object } b && double.TryParse(J.S(b, "amount"), CultureInfo.InvariantCulture, out var amt) && amt != 0
                    ? new AppItem(J.S(f, "id") ?? "", J.T(f, "{first_name} {last_name}"), amt > 0 ? $"owes you {J.S(b, "amount")} {J.S(b, "currency_code")}" : $"you owe {Math.Abs(amt).ToString("0.##", CultureInfo.InvariantCulture)} {J.S(b, "currency_code")}", null, amt > 0 ? "owes you" : "you owe") : null, 100); } },
            new ViewSpec { Id = "expenses", Label = "Recent expenses", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(SwApi + "get_expenses?limit=25");
                return J.Items(d.RootElement, "expenses", e => J.S(e, "deleted_at") is { Length: > 0 } ? null : new AppItem(J.S(e, "id") ?? "", $"{J.S(e, "description")} · {J.S(e, "cost")} {J.S(e, "currency_code")}", J.When(J.S(e, "date")))); } },
            new ViewSpec { Id = "all-friends", Label = "Friends", Load = async (c, _) => { using var d = await c.GetAsync(SwApi + "get_friends"); return J.Items(d.RootElement, "friends", f => new AppItem(J.S(f, "id") ?? "", J.T(f, "{first_name} {last_name}"), J.S(f, "email")), 200); } },
            new ViewSpec { Id = "groups", Label = "Groups", Load = async (c, _) => { using var d = await c.GetAsync(SwApi + "get_groups"); return J.Items(d.RootElement, "groups", g => new AppItem(J.S(g, "id") ?? "", J.S(g, "name") ?? "Group", $"{J.Arr(g, "members").Count()} people")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "split", Label = "Split an expense with a friend", Hint = "You paid; it is split equally between you and the friend.", Inputs = [new("friend", "Friend", "select-view", null, true, null, "all-friends"), F("description", "What was it for?"), F("cost", "Total amount", "number"), F("currency", "Currency", def: "INR")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["friend"])) throw new AppException(400, "Choose a friend."); if (!decimal.TryParse(i["cost"], NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) || cost <= 0) throw new AppException(400, "Enter the amount as a number above zero.");
                    var half = Math.Round(cost / 2, 2); var rest = cost - half; string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                    using var d = await c.PostAsync(SwApi + "create_expense", new Dictionary<string, object> { ["cost"] = N(cost), ["description"] = i["description"], ["currency_code"] = i["currency"].ToUpperInvariant(), ["group_id"] = 0,
                        ["users__0__user_id"] = long.Parse(c.Account.AccountId), ["users__0__paid_share"] = N(cost), ["users__0__owed_share"] = N(rest), ["users__1__user_id"] = long.Parse(i["friend"]), ["users__1__paid_share"] = "0.00", ["users__1__owed_share"] = N(half) });
                    if (J.Arr(d.RootElement, "errors.base").FirstOrDefault().ValueKind == JsonValueKind.String) throw new AppException(400, $"Splitwise said: {J.Arr(d.RootElement, "errors.base").First().GetString()}"); return new ActionResult("Expense added."); } },
        ],
    };

    // ============================================================================================
    // Wise (personal API token; read-only)
    // ============================================================================================
    private const string WiseApi = "https://api.wise.com/";

    private static ProviderApp Wise() => new()
    {
        Key = "wise", Name = "Wise", Icon = "mono:WS", Color = "#9FE870", Account = "Balances and transfers",
        ConnectTitle = "Connect Wise", ConnectLabel = "Connect Wise", ConnectBlurb = "Create a read-only personal API token (Wise → Settings → Integrations and tools → API tokens) and paste it here.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#wise",
        Ask = Rx("wise|transferwise|balances?|transfers?|exchange rate"),
        Setup = ["Nothing to set on the server. Each person creates a **read-only API token** in Wise (Settings → Integrations and tools → API tokens)."],
        Notes = "In the UK and EEA, Wise requires strong customer authentication for some calls, so balances may be refused for personal tokens.",
        Credentials = new()
        {
            Fields = [F("token", "API token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(WiseApi + "v1/profiles"); var p = J.Arr(d.RootElement, "").FirstOrDefault(x => J.S(x, "type") == "personal"); if (p.ValueKind != JsonValueKind.Object) p = J.Arr(d.RootElement, "").FirstOrDefault(); if (p.ValueKind != JsonValueKind.Object) throw new AppException(400, "That token has no Wise profile.");
                return new ProfileInfo(J.S(p, "id") ?? "", J.First(p, "fullName", "details.firstName") ?? "Wise", new() { ["profile"] = J.S(p, "id") ?? "" }); },
        },
        Views =
        [
            new ViewSpec { Id = "balances", Label = "Balances", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(WiseApi + $"v4/profiles/{c.Extra("profile")}/balances?types=STANDARD");
                return J.Items(d.RootElement, "", b => new AppItem(J.S(b, "id") ?? J.S(b, "currency") ?? "", J.S(b, "currency") ?? "Balance", null, null, $"{J.S(b, "amount.value")} {J.S(b, "amount.currency")}")); } },
            new ViewSpec { Id = "transfers", Label = "Recent transfers", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(WiseApi + $"v1/transfers?profile={c.Extra("profile")}&limit=25");
                return J.Items(d.RootElement, "", t => new AppItem(J.S(t, "id") ?? "", $"{J.S(t, "sourceValue")} {J.S(t, "sourceCurrency")} → {J.S(t, "targetValue")} {J.S(t, "targetCurrency")}", $"{J.First(t, "details.reference", "reference")} · {J.When(J.S(t, "created"))}", null, J.S(t, "status")?.Replace('_', ' '))); } },
        ],
    };

    // ============================================================================================
    // Zerodha Kite (read-only; the login lasts until about 6 a.m. India time the next day)
    // ============================================================================================
    private const string KiteApi = "https://api.kite.trade/";

    private static ProviderApp Kite() => new()
    {
        Key = "zerodha-kite", Name = "Zerodha Kite", Icon = "mono:ZK", Color = "#387ED1", Account = "Holdings, positions and funds",
        ConnectTitle = "Connect Zerodha Kite", ConnectLabel = "Log in with Zerodha", ConnectBlurb = "Log in with Zerodha and Daksha shows your holdings, positions and available funds. Read-only: Daksha never places orders. Zerodha logins expire every morning, so you reconnect daily.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads. It never places orders.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#zerodha-kite",
        Ask = Rx("zerodha|kite|holdings?|positions?|portfolio|stocks?|mutual funds?"),
        NoBearer = true, Decorate = (r, c) => { r.Headers.TryAddWithoutValidation("X-Kite-Version", "3"); if (c.Token is not null) r.Headers.TryAddWithoutValidation("Authorization", $"token {c.Server("Kite:OAuth:ClientId")}:{c.Token}"); },
        Setup = ["Create a Kite Connect app at developers.kite.trade (a paid Zerodha developer subscription). Set its **Redirect URL** to `<API address>/api/integrations/zerodha-kite/callback`. Copy the **API key** (as the client id) and **API secret**."],
        Notes = "Kite access tokens expire around 6 a.m. India time every day, so the person logs in again each morning. This app only reads.",
        OAuth = new()
        {
            Section = "Kite", AuthUrl = "https://kite.zerodha.com/connect/login", TokenUrl = KiteApi + "session/token", CodeParam = "request_token",
            BuildAuthUrl = (_, c, state, _, _, _) => QueryHelpers.AddQueryString("https://kite.zerodha.com/connect/login", new Dictionary<string, string?> { ["v"] = "3", ["api_key"] = c.ClientId, ["redirect_params"] = "state=" + Uri.EscapeDataString(state) }),
            Exchange = async x =>
            {
                var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(x.Config.ClientId + x.Code + (x.Config.ClientSecret ?? ""))));
                using var req = new HttpRequestMessage(HttpMethod.Post, KiteApi + "session/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["api_key"] = x.Config.ClientId, ["request_token"] = x.Code, ["checksum"] = checksum }) };
                req.Headers.TryAddWithoutValidation("X-Kite-Version", "3");
                using var res = await x.Clients.CreateClient("provider-apps").SendAsync(req, x.Ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(x.Ct));
                var token = J.S(doc.RootElement, "data.access_token"); if (token is null) return null;
                var now = DateTimeOffset.UtcNow; var expiry = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 30, 0, TimeSpan.Zero); if (expiry <= now.AddMinutes(10)) expiry = expiry.AddDays(1);
                return new TokenSet(token, null, expiry, new() { ["user_id"] = J.S(doc.RootElement, "data.user_id") ?? "" });
            },
        },
        Profile = async c => { using var d = await c.GetAsync(KiteApi + "user/profile"); return new ProfileInfo(J.S(d.RootElement, "data.user_id") ?? "", J.First(d.RootElement, "data.user_name", "data.user_id") ?? "Zerodha"); },
        Views =
        [
            new ViewSpec { Id = "holdings", Label = "Holdings", Dashboard = true, Empty = "You hold no stocks right now.", Load = async (c, _) => { using var d = await c.GetAsync(KiteApi + "portfolio/holdings");
                return J.Items(d.RootElement, "data", h => new AppItem(J.S(h, "tradingsymbol") ?? "", $"{J.S(h, "tradingsymbol")} · {J.S(h, "quantity")} shares", $"Avg {J.S(h, "average_price")} · last {J.S(h, "last_price")} · day {J.S(h, "day_change_percentage")}%", null, $"P&L {J.S(h, "pnl")}"), 100); } },
            new ViewSpec { Id = "positions", Label = "Positions", Empty = "No open positions.", Load = async (c, _) => { using var d = await c.GetAsync(KiteApi + "portfolio/positions");
                return J.Items(d.RootElement, "data.net", p => new AppItem(J.S(p, "tradingsymbol") ?? "", $"{J.S(p, "tradingsymbol")} · {J.S(p, "quantity")}", $"{J.S(p, "product")} · last {J.S(p, "last_price")}", null, $"P&L {J.S(p, "pnl")}"), 100); } },
            new ViewSpec { Id = "funds", Label = "Funds", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(KiteApi + "user/margins");
                return [new AppItem("equity", "Equity", $"Used {J.S(d.RootElement, "data.equity.utilised.debits") ?? "0"}", null, $"{J.S(d.RootElement, "data.equity.available.live_balance") ?? J.S(d.RootElement, "data.equity.net") ?? "0"} available")]; } },
        ],
    };

    // ============================================================================================
    // Etsy (read-only: orders and listings)
    // ============================================================================================
    private const string EtsyApi = "https://api.etsy.com/v3/application/";

    private static string EtsyMoney(JsonElement e, string path) => double.TryParse(J.S(e, path + ".amount"), CultureInfo.InvariantCulture, out var a) && double.TryParse(J.S(e, path + ".divisor"), CultureInfo.InvariantCulture, out var d) && d > 0 ? $"{(a / d).ToString("0.00", CultureInfo.InvariantCulture)} {J.S(e, path + ".currency_code")}" : "";

    private static ProviderApp Etsy() => new()
    {
        Key = "etsy", Name = "Etsy", Icon = "mono:ET", Color = "#F1641E", Account = "Orders and listings",
        ConnectTitle = "Connect your Etsy shop", ConnectLabel = "Continue with Etsy", ConnectBlurb = "Sign in with Etsy and Daksha shows your shop's recent orders and active listings. Read-only.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#etsy",
        Ask = Rx("etsy|my shop|etsy orders?|listings?"),
        NoBearer = false, Decorate = (r, c) => r.Headers.TryAddWithoutValidation("x-api-key", c.Server("Etsy:OAuth:ClientId")),
        Setup = ["etsy.com/developers/your-apps → **Create a new app**. Copy the **keystring** (client id). Add the redirect URI `<API address>/api/integrations/etsy/callback`.", "Etsy reviews new apps before they can be used by other people; until then only your own shop works."],
        Notes = "Etsy's personal-use access is granted quickly; commercial access needs Etsy's approval.",
        OAuth = new() { Section = "Etsy", AuthUrl = "https://www.etsy.com/oauth/connect", TokenUrl = "https://api.etsy.com/v3/public/oauth/token", Pkce = true, SecretOptional = true, ReadScopes = ["shops_r", "transactions_r", "listings_r"] },
        Profile = async c => { using var d = await c.GetAsync(EtsyApi + "users/me"); var shop = J.S(d.RootElement, "shop_id") ?? throw new AppException(400, "That Etsy account has no shop."); return new ProfileInfo(J.S(d.RootElement, "user_id") ?? "", $"Etsy shop {shop}", new() { ["shop"] = shop }); },
        Views =
        [
            new ViewSpec { Id = "orders", Label = "Recent orders", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(EtsyApi + $"shops/{c.Extra("shop")}/receipts?limit=25&sort_on=created&sort_order=desc");
                return J.Items(d.RootElement, "results", r => new AppItem(J.S(r, "receipt_id") ?? "", $"{J.S(r, "name")} · {EtsyMoney(r, "grandtotal")}", J.When(J.S(r, "created_timestamp")), $"https://www.etsy.com/your/orders/sold/{J.S(r, "receipt_id")}", J.S(r, "status"))); } },
            new ViewSpec { Id = "listings", Label = "Active listings", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(EtsyApi + $"shops/{c.Extra("shop")}/listings?state=active&limit=25");
                return J.Items(d.RootElement, "results", l => new AppItem(J.S(l, "listing_id") ?? "", J.S(l, "title") ?? "Listing", $"{EtsyMoney(l, "price")} · {J.S(l, "quantity")} in stock", J.S(l, "url"))); } },
        ],
    };

    // ============================================================================================
    // HubSpot (CRM)
    // ============================================================================================
    private const string HsApi = "https://api.hubapi.com/";

    private static ProviderApp HubSpot() => new()
    {
        Key = "hubspot", Name = "HubSpot", Icon = "mono:HS", Color = "#FF7A59", Account = "Contacts and deals",
        ConnectTitle = "Connect HubSpot", ConnectLabel = "Continue with HubSpot", ConnectBlurb = "Sign in with HubSpot and Daksha shows your contacts and deals.",
        WriteLabel = "Allow Daksh to create contacts", WriteHint = "Full access lets Daksha create contacts.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#hubspot",
        Ask = Rx("hubspot|crm|deals?|contacts?|leads?"),
        Setup = ["developers.hubspot.com → **Create app** (Public app). On the Auth tab add the redirect URL `<API address>/api/integrations/hubspot/callback` and the scopes `crm.objects.contacts.read`, `crm.objects.deals.read` and `crm.objects.contacts.write`. Copy the client id and secret."],
        OAuth = new() { Section = "HubSpot", AuthUrl = "https://app.hubspot.com/oauth/authorize", TokenUrl = HsApi + "oauth/v1/token", ReadScopes = ["oauth", "crm.objects.contacts.read", "crm.objects.deals.read"], WriteScopes = ["crm.objects.contacts.write"] },
        Profile = async c => { using var d = await c.GetAsync(HsApi + "account-info/v3/details"); var id = J.S(d.RootElement, "portalId") ?? throw new AppException(502, "HubSpot did not return the account."); return new ProfileInfo(id, $"HubSpot account {id}"); },
        Views =
        [
            new ViewSpec { Id = "contacts", Label = "Contacts", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(HsApi + "crm/v3/objects/contacts?limit=25&properties=firstname,lastname,email,company");
                return J.Items(d.RootElement, "results", x => new AppItem(J.S(x, "id") ?? "", J.T(x, "{properties.firstname} {properties.lastname}") is { Length: > 0 } n ? n : J.S(x, "properties.email") ?? "Contact", $"{J.S(x, "properties.email")} · {J.S(x, "properties.company")}".Trim(' ', '·'))); } },
            new ViewSpec { Id = "deals", Label = "Deals", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(HsApi + "crm/v3/objects/deals?limit=25&properties=dealname,amount,dealstage,closedate");
                return J.Items(d.RootElement, "results", x => new AppItem(J.S(x, "id") ?? "", J.S(x, "properties.dealname") ?? "Deal", $"{J.S(x, "properties.amount")} · closes {J.When(J.S(x, "properties.closedate"))}", null, J.S(x, "properties.dealstage"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "contact", Label = "Create a contact", Inputs = [F("email", "Email"), F("firstname", "First name", required: false), F("lastname", "Last name", required: false)],
                Run = async (c, i) => { if (!Regex.IsMatch(i["email"], @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new AppException(400, "Enter a valid email address."); using var _ = await c.PostAsync(HsApi + "crm/v3/objects/contacts", new { properties = new { email = i["email"], firstname = i["firstname"], lastname = i["lastname"] } }); return new ActionResult("Contact created."); } },
        ],
    };
}
