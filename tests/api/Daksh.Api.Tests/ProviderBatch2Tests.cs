using System.Net;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ProviderBatch2Tests
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

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? cred = null, Dictionary<string, string>? extra = null, Dictionary<string, string>? settings = null, string? token = null, string accountId = "acct", IConfiguration? config = null) => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = config ?? new ConfigurationBuilder().Build(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = settings ?? [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = accountId, Extra = extra ?? [] },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<IReadOnlyList<AppItem>> Load(ProviderCtx c, string view, params (string, string)[] input) =>
        ProviderApps.Get(c.App.Key).Views.Single(v => v.Id == view).Load(c, input.ToDictionary(i => i.Item1, i => i.Item2));
    private static Task<ActionResult> Run(ProviderCtx c, string action, params (string, string)[] input) =>
        ProviderApps.Get(c.App.Key).Actions.Single(a => a.Id == action).Run(c, input.ToDictionary(i => i.Item1, i => i.Item2));

    [Fact]
    public async Task GitLab_sends_its_own_token_header_and_reads_the_right_server()
    {
        var h = new Handler(_ => Json("""[{"id":1,"title":"Fix login","references":{"full":"grp/app!7"},"author":{"username":"asha"},"updated_at":"2026-12-01T00:00:00Z","web_url":"https://git.example.com/grp/app/-/merge_requests/7","merge_status":"can_be_merged","draft":false}]"""));
        var c = Ctx("gitlab", h, cred: new() { ["url"] = "https://git.example.com", ["token"] = "glpat" }, extra: new() { ["username"] = "amit" });
        var items = await Load(c, "assigned");
        Assert.Equal("glpat", h.Seen[0].Request.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Null(h.Seen[0].Request.Headers.Authorization);
        Assert.StartsWith("https://git.example.com/api/v4/merge_requests", h.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("Fix login", Assert.Single(items).Title);
        await Load(c, "review");
        Assert.Contains("reviewer_username=amit", h.Seen[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task GitLab_comment_refuses_odd_project_names_and_encodes_good_ones()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("gitlab", h, cred: new() { ["url"] = "https://gitlab.com", ["token"] = "t" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "comment", ("project", "a b;c"), ("type", "issues"), ("iid", "3"), ("text", "x")));
        await Run(c, "comment", ("project", "grp/app"), ("type", "merge_requests"), ("iid", "3"), ("text", "Looks good"));
        Assert.EndsWith("/projects/grp%2Fapp/merge_requests/3/notes", h.Seen[0].Request.RequestUri!.AbsoluteUri);
        Assert.Contains("Looks good", h.Seen[0].Body);
    }

    [Fact]
    public async Task Jira_uses_basic_auth_escapes_search_text_and_writes_rich_text()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"issues":[{"key":"ABC-1","fields":{"summary":"Broken","status":{"name":"To Do"},"priority":{"name":"High"},"updated":"2026-12-01T00:00:00Z"}}]}""") : Json("""{"key":"ABC-9"}"""));
        var c = Ctx("jira", h, cred: new() { ["site"] = "team.atlassian.net", ["email"] = "a@b.co", ["token"] = "tok" });
        var items = await Load(c, "search", ("q", "say \"hi\""));
        Assert.Equal("Basic", h.Seen[0].Request.Headers.Authorization!.Scheme);
        Assert.Equal("a@b.co:tok", Encoding.UTF8.GetString(Convert.FromBase64String(h.Seen[0].Request.Headers.Authorization!.Parameter!)));
        Assert.DoesNotContain("%22hi%22", h.Seen[0].Request.RequestUri!.Query); // quotes inside the text cannot break out of the JQL string
        Assert.Equal("ABC-1 · Broken", Assert.Single(items).Title);
        var r = await Run(c, "create", ("project", "ABC"), ("summary", "Do it"), ("description", "Details"), ("type", "Task"));
        Assert.Equal("https://team.atlassian.net/browse/ABC-9", r.Url);
        using var body = JsonDocument.Parse(h.Seen[^1].Body);
        Assert.Equal("doc", body.RootElement.GetProperty("fields").GetProperty("description").GetProperty("type").GetString());
        await Assert.ThrowsAsync<AppException>(() => Run(c, "create", ("project", "abc; drop"), ("summary", "x"), ("description", ""), ("type", "Task")));
    }

    [Fact]
    public async Task Stripe_payment_link_creates_a_product_a_price_then_the_link_in_minor_units()
    {
        var step = 0;
        var h = new Handler(_ => Json(step++ switch { 0 => """{"id":"prod_1"}""", 1 => """{"id":"price_1"}""", _ => """{"url":"https://buy.stripe.com/abc"}""" }));
        var c = Ctx("stripe", h, cred: new() { ["key"] = "rk_test_" + new string('a', 20) });
        var r = await Run(c, "payment-link", ("name", "Consulting"), ("amount", "499.50"), ("currency", "inr"));
        Assert.Equal("https://buy.stripe.com/abc", r.Url);
        Assert.Equal(3, h.Seen.Count);
        Assert.Contains("unit_amount=49950", h.Seen[1].Body);
        Assert.Contains("product=prod_1", h.Seen[1].Body);
        Assert.Contains("line_items%5B0%5D%5Bprice%5D=price_1", h.Seen[2].Body);
        Assert.Equal("Bearer", h.Seen[0].Request.Headers.Authorization!.Scheme);
    }

    [Theory]
    [InlineData("49900", "INR", "499.00 INR")]
    [InlineData("1500", "jpy", "1,500 JPY")]
    [InlineData("5", "usd", "0.05 USD")]
    public async Task Amounts_are_shown_in_major_units_with_the_right_decimals(string minor, string currency, string expected)
    {
        var h = new Handler(_ => Json($$"""{"data":[{"id":"ch_1","amount":{{minor}},"currency":"{{currency}}","status":"succeeded","created":1700000000}]}"""));
        var items = await Load(Ctx("stripe", h, cred: new() { ["key"] = "sk_test_" + new string('b', 20) }), "charges");
        Assert.Equal(expected, Assert.Single(items).Title);
    }

    [Fact]
    public async Task Stripe_key_must_look_like_a_stripe_key()
    {
        var prepare = ProviderApps.Get("stripe").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["key"] = "pk_live_publishable_key_12345" }, CancellationToken.None));
        await prepare(new Dictionary<string, string> { ["key"] = "rk_live_" + new string('x', 24) }, CancellationToken.None);
    }

    [Fact]
    public async Task Razorpay_shows_rupees_and_refuses_a_malformed_key()
    {
        var h = new Handler(_ => Json("""{"items":[{"id":"pay_1","amount":49900,"currency":"INR","status":"captured","method":"upi","email":"a@b.co","created_at":1700000000}]}"""));
        var c = Ctx("razorpay", h, cred: new() { ["keyId"] = "rzp_test_abcdefgh12", ["keySecret"] = "s3cret" });
        Assert.Equal("499.00 INR", (await Load(c, "payments")).Single().Title);
        Assert.Equal("Basic", h.Seen[0].Request.Headers.Authorization!.Scheme);
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("razorpay").Credentials!.Prepare!(new Dictionary<string, string> { ["keyId"] = "not-a-key" }, CancellationToken.None));
        var r = await Run(Ctx("razorpay", new Handler(_ => Json("""{"short_url":"https://rzp.io/i/abc"}""")), cred: new() { ["keyId"] = "rzp_test_abcdefgh12", ["keySecret"] = "s" }), "payment-link", ("amount", "12.5"), ("description", "Fee"), ("name", ""), ("email", ""));
        Assert.Equal("https://rzp.io/i/abc", r.Url);
    }

    [Fact]
    public async Task PayPal_logs_in_with_client_credentials_for_the_chosen_mode()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/oauth2/token") ? Json("""{"access_token":"AT"}""") : Json("""{"transaction_details":[{"transaction_info":{"transaction_id":"T1","transaction_amount":{"value":"20.00","currency_code":"USD"},"transaction_status":"S","transaction_initiation_date":"2026-12-01T00:00:00Z"},"payer_info":{"email_address":"p@x.co"}}]}"""));
        var c = Ctx("paypal", h, cred: new() { ["clientId"] = "cid", ["secret"] = "sec", ["mode"] = "sandbox" });
        var items = await Load(c, "transactions");
        Assert.Equal("api-m.sandbox.paypal.com", h.Seen[0].Request.RequestUri!.Host);
        Assert.Contains("grant_type=client_credentials", h.Seen[0].Body);
        Assert.Equal("AT", h.Seen[1].Request.Headers.Authorization!.Parameter);
        Assert.Equal("20.00 USD", Assert.Single(items).Title);
    }

    [Fact]
    public async Task Zoho_uses_its_own_authorization_scheme_and_the_configured_region()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/folders") ? Json("""{"data":[{"folderId":"11","folderType":"Inbox"},{"folderId":"12","folderType":"Sent"}]}""") : Json("""{"data":[{"messageId":"900","subject":"Hello","sender":"A","receivedTime":"1700000000000","status":"0"}]}"""));
        var c = Ctx("zoho-mail", h, token: "ZT", accountId: "555", config: new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZohoMail:Region"] = "in" }).Build());
        var items = await Load(c, "inbox");
        Assert.Equal("Zoho-oauthtoken ZT", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        Assert.Equal("mail.zoho.in", h.Seen[0].Request.RequestUri!.Host);
        Assert.Contains("folderId=11", h.Seen[1].Request.RequestUri!.Query);
        Assert.Equal("11/900", Assert.Single(items).Id); Assert.Equal("unread", items[0].Badge); Assert.Equal("message", items[0].Drill);
    }

    [Fact]
    public void Region_urls_only_accept_a_plain_domain_suffix()
    {
        var zoho = ProviderApps.Get("zoho-mail");
        string Url(string? region) => ProviderEngine.RegionUrl(zoho.OAuth!, zoho.OAuth!.AuthUrl, new ConfigurationBuilder().AddInMemoryCollection(region is null ? [] : new Dictionary<string, string?> { ["ZohoMail:Region"] = region }).Build());
        Assert.Equal("https://accounts.zoho.com/oauth/v2/auth", Url(null));
        Assert.Equal("https://accounts.zoho.in/oauth/v2/auth", Url("in"));
        Assert.Equal("https://accounts.zoho.com.au/oauth/v2/auth", Url("com.au"));
        Assert.Equal("https://accounts.zoho.com/oauth/v2/auth", Url("evil.com/x?")); // anything odd falls back to the default
    }

    [Fact]
    public async Task Dropbox_entries_are_read_by_their_dot_tag_and_folders_drill_in()
    {
        var h = new Handler(_ => Json("""{"entries":[{".tag":"folder","name":"Work","path_lower":"/work","path_display":"/Work"},{".tag":"file","name":"a.pdf","path_lower":"/a.pdf","path_display":"/a.pdf","size":2097152,"client_modified":"2026-12-01T00:00:00Z"}]}"""));
        var items = await Load(Ctx("dropbox", h, token: "T"), "files");
        Assert.Equal("folder", items[0].Drill); Assert.Equal("/work", items[0].Id); Assert.Null(items[1].Drill);
        Assert.StartsWith("2 MB", items[1].Subtitle);
        Assert.Equal("https://www.dropbox.com/home/a.pdf", items[1].Url);
        await Assert.ThrowsAsync<AppException>(() => Load(Ctx("dropbox", h, token: "T"), "folder", ("arg", "/../secret")));
    }

    [Fact]
    public async Task Dropbox_account_lookup_sends_no_body_and_no_content_type()
    {
        var h = new Handler(_ => Json("""{"account_id":"dbid:1","name":{"display_name":"Amit"}}"""));
        var p = await ProviderApps.Get("dropbox").Profile!(Ctx("dropbox", h, token: "T"));
        Assert.Equal("Amit", p.Name);
        Assert.Null(h.Seen[0].Request.Content);
    }

    [Fact]
    public async Task Airtable_walks_from_bases_to_tables_to_records_and_checks_added_json()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/tables") ? Json("""{"tables":[{"id":"tblAAAAAAAAAAAA","name":"People","fields":[{},{}]}]}""") : Json("""{"records":[{"id":"recBBBBBBBBBBBB","fields":{"Name":"Alice","Status":"Todo","Age":30}}]}"""));
        var c = Ctx("airtable", h, cred: new() { ["token"] = "pat" });
        var tables = await Load(c, "tables", ("arg", "appAAAAAAAAAAAA"));
        Assert.Equal("appAAAAAAAAAAAA/tblAAAAAAAAAAAA", tables[0].Id); Assert.Equal("records", tables[0].Drill);
        var records = await Load(c, "records", ("arg", tables[0].Id));
        Assert.Equal("Alice", records[0].Title); Assert.Contains("Status: Todo", records[0].Subtitle);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "records", ("arg", "../../etc")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "add-record", ("base", "appAAAAAAAAAAAA"), ("table", "People"), ("fields", "not json")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "add-record", ("base", "appAAAAAAAAAAAA"), ("table", "People"), ("fields", "[1,2]")));
        await Run(c, "add-record", ("base", "appAAAAAAAAAAAA"), ("table", "People"), ("fields", """{"Name":"Bob"}"""));
        Assert.Contains("\"Bob\"", h.Seen[^1].Body);
    }

    [Fact]
    public async Task Cloudflare_checks_zone_ids_before_calling()
    {
        var h = new Handler(_ => Json("""{"result":[{"id":"abc","name":"example.com","status":"active"}]}"""));
        var c = Ctx("cloudflare", h, cred: new() { ["token"] = "cf" });
        await Assert.ThrowsAsync<AppException>(() => Load(c, "dns", ("arg", "../../user")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "purge", ("zone", "nope")));
        Assert.Empty(h.Seen);
        await Run(c, "purge", ("zone", new string('a', 32)));
        Assert.Equal("Bearer", h.Seen[0].Request.Headers.Authorization!.Scheme);
        Assert.Contains("purge_everything", h.Seen[0].Body);
    }

    [Fact]
    public async Task Docker_hub_signs_in_first_then_reads_repositories()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/auth/token") ? Json("""{"access_token":"JWT"}""") : Json("""{"results":[{"name":"api","pull_count":10,"star_count":2,"last_updated":"2026-12-01T00:00:00Z"}]}"""));
        var items = await Load(Ctx("docker-hub", h, cred: new() { ["username"] = "amit", ["token"] = "dckr" }, accountId: "amit"), "repositories");
        Assert.Contains("\"identifier\":\"amit\"", h.Seen[0].Body);
        Assert.Equal("JWT", h.Seen[1].Request.Headers.Authorization!.Parameter);
        Assert.Equal("tags", Assert.Single(items).Drill);
    }

    [Fact]
    public async Task Zoom_schedules_in_the_persons_time_zone_converted_to_utc()
    {
        var h = new Handler(_ => Json("""{"join_url":"https://zoom.us/j/1"}"""));
        var c = Ctx("zoom", h, token: "T", settings: new() { ["timeZone"] = "Asia/Kolkata" });
        await Run(c, "schedule", ("topic", "Sync"), ("start", "2026-12-01T10:00"), ("minutes", "45"));
        using var body = JsonDocument.Parse(h.Seen[0].Body);
        Assert.Equal("2026-12-01T04:30:00Z", body.RootElement.GetProperty("start_time").GetString());
        Assert.Equal(45, body.RootElement.GetProperty("duration").GetInt32());
        await Assert.ThrowsAsync<AppException>(() => Run(Ctx("zoom", h, token: "T", settings: new() { ["timeZone"] = "Mars/Base" }), "schedule", ("topic", "x"), ("start", "2026-12-01T10:00"), ("minutes", "30")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "schedule", ("topic", "x"), ("start", "2026-12-01T10:00"), ("minutes", "0")));
    }

    [Fact]
    public async Task Self_hosted_addresses_are_checked_when_the_details_are_saved()
    {
        var prepare = ProviderApps.Get("n8n").Credentials!.Prepare!;
        var ok = new Dictionary<string, string> { ["url"] = "n8n.example.com/", ["key"] = "k" };
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["url"] = "http://169.254.169.254/", ["key"] = "k" }, CancellationToken.None));
        await prepare(new Dictionary<string, string> { ["url"] = "http://192.168.1.20:5678/", ["key"] = "k" }, CancellationToken.None);
        var gl = ProviderApps.Get("gitlab").Credentials!.Prepare!;
        var v = new Dictionary<string, string> { ["url"] = "http://10.0.0.5:8080/gitlab/", ["token"] = "t" };
        await gl(v, CancellationToken.None);
        Assert.Equal("http://10.0.0.5:8080/gitlab", v["url"]);
        _ = ok;
    }

    [Fact]
    public async Task Calendly_lists_meetings_by_uuid_so_they_can_be_cancelled()
    {
        var h = new Handler(_ => Json("""{"collection":[{"uri":"https://api.calendly.com/scheduled_events/AAAA-1111","name":"Intro call","start_time":"2026-12-01T10:00:00Z","location":{"type":"zoom","join_url":"https://zoom.us/j/9"}}]}"""));
        var c = Ctx("calendly", h, cred: new() { ["token"] = "pat" }, extra: new() { ["uri"] = "https://api.calendly.com/users/U1" });
        var items = await Load(c, "upcoming");
        Assert.Equal("AAAA-1111", items[0].Id);
        Assert.Contains("user=https%3A%2F%2Fapi.calendly.com%2Fusers%2FU1", h.Seen[0].Request.RequestUri!.Query);
        await Run(c, "cancel", ("event", "AAAA-1111"), ("reason", "Clash"));
        Assert.EndsWith("/scheduled_events/AAAA-1111/cancellation", h.Seen[1].Request.RequestUri!.AbsolutePath);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "cancel", ("event", "../x"), ("reason", "")));
    }
}
