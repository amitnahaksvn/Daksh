using System.Net;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ProviderBatch6Tests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? cred = null, Dictionary<string, string>? extra = null, string? token = null, string accountId = "acct") => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Etsy:OAuth:ClientId"] = "etsykey", ["Kite:OAuth:ClientId"] = "kitekey" }).Build(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = accountId, Extra = extra ?? [] },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<IReadOnlyList<AppItem>> Load(ProviderCtx c, string view, params (string, string)[] input) => c.App.Views.Single(v => v.Id == view).Load(c, input.ToDictionary(i => i.Item1, i => i.Item2));
    private static Task<ActionResult> Run(ProviderCtx c, string action, params (string, string)[] input) => c.App.Actions.Single(a => a.Id == action).Run(c, input.ToDictionary(i => i.Item1, i => i.Item2));

    [Fact]
    public void All_fifteen_apps_are_registered_and_read_only_ones_have_no_actions()
    {
        string[] keys = ["todoist", "trello", "asana", "clickup", "linear", "google-tasks", "strava", "hardcover", "readwise", "plex", "splitwise", "wise", "zerodha-kite", "etsy", "hubspot"];
        foreach (var k in keys) Assert.NotNull(ProviderApps.All.SingleOrDefault(a => a.Key == k));
        foreach (var k in new[] { "strava", "hardcover", "readwise", "plex", "wise", "zerodha-kite", "etsy" }) { var a = ProviderApps.Get(k); Assert.False(a.HasWrite); Assert.Empty(a.Actions); }
    }

    [Fact]
    public async Task Todoist_reads_a_results_page_and_closes_only_valid_ids()
    {
        var h = new Handler(_ => Json("""{"results":[{"id":"abc123","content":"Pay rent","due":{"string":"today"},"priority":4}]}"""));
        var c = Ctx("todoist", h, new() { ["token"] = "T" });
        var t = (await Load(c, "today")).Single(); Assert.Equal("Pay rent", t.Title); Assert.Contains("urgent", t.Subtitle);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "complete", ("task", "../x")));
    }

    [Fact]
    public async Task Trello_sends_oauth_header_and_rejects_bad_ids()
    {
        var h = new Handler(_ => Json("""[{"id":"0123456789abcdef01234567","name":"Card","closed":false,"url":"https://trello.com/c/x"}]"""));
        var c = Ctx("trello", h, new() { ["key"] = "K", ["token"] = "T" });
        Assert.Single(await Load(c, "mine"));
        Assert.Contains("oauth_consumer_key=\"K\"", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => Load(c, "cards", ("arg", "../1")));
    }

    [Fact]
    public async Task Linear_sends_the_key_without_bearer_and_validates_identifiers()
    {
        var h = new Handler(_ => Json("""{"data":{"viewer":{"assignedIssues":{"nodes":[{"identifier":"ENG-1","title":"Fix","url":"https://linear.app/x","updatedAt":"2026-12-01T00:00:00Z","state":{"name":"Todo"},"team":{"key":"ENG"}}]}}}}"""));
        var c = Ctx("linear", h, new() { ["key"] = "lin_api_x" });
        Assert.Contains("ENG-1", (await Load(c, "assigned")).Single().Title);
        Assert.Equal("lin_api_x", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => Run(c, "comment", ("issue", "x\" } mutation"), ("text", "hi")));
        var bad = Ctx("linear", new Handler(_ => Json("""{"errors":[{"message":"nope"}]}""")), new() { ["key"] = "k" });
        await Assert.ThrowsAsync<AppException>(() => Load(bad, "assigned"));
    }

    [Fact]
    public async Task Hardcover_accepts_a_token_pasted_with_bearer_prefix()
    {
        var h = new Handler(_ => Json("""{"data":{"me":[{"user_books":[{"rating":4,"book":{"title":"Dune","slug":"dune","contributions":[{"author":{"name":"Frank Herbert"}}]}}]}]}}"""));
        var c = Ctx("hardcover", h, new() { ["token"] = "Bearer abc" });
        Assert.Equal("Dune", (await Load(c, "reading")).Single().Title);
        Assert.Equal("Bearer abc", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Readwise_uses_the_token_scheme()
    {
        var h = new Handler(_ => Json("""{"highlights":[{"id":1,"text":"Stay hungry","title":"Book","author":"A"}]}"""));
        var c = Ctx("readwise", h, new() { ["token"] = "abc" });
        Assert.Equal("Stay hungry", (await Load(c, "review")).Single().Title);
        Assert.Equal("Token abc", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Asana_creates_a_task_in_the_users_workspace()
    {
        var h = new Handler(_ => Json("""{"data":{"permalink_url":"https://app.asana.com/0/1/2"}}"""));
        var c = Ctx("asana", h, new() { ["token"] = "T" }, new() { ["workspace"] = "99" });
        await Run(c, "create", ("name", "Do it"), ("notes", ""), ("due", ""));
        using var body = JsonDocument.Parse(h.Seen[0].Body);
        Assert.Equal("99", body.RootElement.GetProperty("data").GetProperty("workspace").GetString());
        await Assert.ThrowsAsync<AppException>(() => Run(c, "complete", ("task", "abc")));
    }

    [Fact]
    public async Task ClickUp_converts_millisecond_due_dates_and_checks_list_ids()
    {
        var h = new Handler(_ => Json("""{"tasks":[{"id":"abc1","name":"Ship","url":"https://app.clickup.com/t/abc1","due_date":"1790000000000","list":{"name":"Sprint"},"status":{"status":"open"}}]}"""));
        var c = Ctx("clickup", h, new() { ["token"] = "pk_1" }, new() { ["team"] = "7" }, accountId: "42");
        Assert.Equal("Ship", (await Load(c, "mine")).Single().Title);
        Assert.Equal("pk_1", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => Run(c, "create", ("list", "../x"), ("name", "n"), ("description", "")));
    }

    [Fact]
    public async Task Google_tasks_completes_with_a_patch_and_validates_ids()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("google-tasks", h, token: "T");
        await Run(c, "complete", ("task", "listA/taskB1"));
        Assert.Equal("PATCH", h.Seen[0].Request.Method.Method);
        Assert.EndsWith("/lists/listA/tasks/taskB1", h.Seen[0].Request.RequestUri!.AbsolutePath);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "complete", ("task", "a/../b")));
    }

    [Fact]
    public async Task Strava_totals_use_the_athlete_id()
    {
        var h = new Handler(_ => Json("""{"ytd_run_totals":{"count":10,"distance":50000,"elapsed_time":18000}}"""));
        var c = Ctx("strava", h, token: "T", accountId: "123");
        var items = await Load(c, "totals");
        Assert.Contains("50 km", items[0].Badge);
        Assert.EndsWith("/athletes/123/stats", h.Seen[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Plex_sends_its_token_header()
    {
        var h = new Handler(_ => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"1","title":"Pilot","grandparentTitle":"Show","User":{"title":"amit"},"Player":{"title":"TV","state":"playing"}}]}}"""));
        var c = Ctx("plex", h, new() { ["url"] = "http://10.0.0.5:32400", ["token"] = "PT" });
        Assert.Contains("Show", (await Load(c, "playing")).Single().Title);
        Assert.Equal("PT", h.Seen[0].Request.Headers.GetValues("X-Plex-Token").Single());
    }

    [Fact]
    public async Task Splitwise_splits_equally_and_rejects_bad_amounts()
    {
        var h = new Handler(_ => Json("""{"expenses":[],"errors":{}}"""));
        var c = Ctx("splitwise", h, token: "T", accountId: "5");
        await Run(c, "split", ("friend", "9"), ("description", "Dinner"), ("cost", "101"), ("currency", "inr"));
        using var body = JsonDocument.Parse(h.Seen[0].Body);
        Assert.Equal("50.50", body.RootElement.GetProperty("users__1__owed_share").GetString());
        Assert.Equal("50.50", body.RootElement.GetProperty("users__0__owed_share").GetString());
        await Assert.ThrowsAsync<AppException>(() => Run(c, "split", ("friend", "9"), ("description", "x"), ("cost", "-3"), ("currency", "INR")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "split", ("friend", "x"), ("description", "x"), ("cost", "3"), ("currency", "INR")));
    }

    [Fact]
    public async Task Wise_lists_balances_for_the_profile()
    {
        var h = new Handler(_ => Json("""[{"id":1,"currency":"EUR","amount":{"value":12.5,"currency":"EUR"}}]"""));
        var c = Ctx("wise", h, new() { ["token"] = "T" }, new() { ["profile"] = "77" });
        Assert.Equal("EUR", (await Load(c, "balances")).Single().Title);
        Assert.Contains("/profiles/77/balances", h.Seen[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Kite_sends_key_and_token_and_the_version_header()
    {
        var h = new Handler(_ => Json("""{"data":[{"tradingsymbol":"INFY","quantity":3,"average_price":1500,"last_price":1600,"pnl":300,"day_change_percentage":1.2}]}"""));
        var c = Ctx("zerodha-kite", h, token: "AT");
        Assert.Contains("INFY", (await Load(c, "holdings")).Single().Title);
        Assert.Equal("token kitekey:AT", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        Assert.Equal("3", h.Seen[0].Request.Headers.GetValues("X-Kite-Version").Single());
    }

    [Fact]
    public async Task Etsy_sends_its_api_key_and_formats_money()
    {
        var h = new Handler(_ => Json("""{"results":[{"receipt_id":5,"name":"Asha","status":"paid","created_timestamp":1790000000,"grandtotal":{"amount":2599,"divisor":100,"currency_code":"USD"}}]}"""));
        var c = Ctx("etsy", h, token: "1234.tok", extra: new() { ["shop"] = "55" });
        Assert.Contains("25.99 USD", (await Load(c, "orders")).Single().Title);
        Assert.Equal("etsykey", h.Seen[0].Request.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task HubSpot_only_creates_contacts_with_a_valid_email()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("hubspot", h, token: "T");
        await Run(c, "contact", ("email", "a@b.co"), ("firstname", "A"), ("lastname", ""));
        Assert.Single(h.Seen);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "contact", ("email", "nope"), ("firstname", ""), ("lastname", "")));
    }
}
