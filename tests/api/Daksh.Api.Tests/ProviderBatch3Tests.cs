using System.Net;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ProviderBatch3Tests
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

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? cred = null, Dictionary<string, string>? extra = null, Dictionary<string, string>? settings = null, string? token = null, string accountId = "acct", Dictionary<string, string?>? config = null) => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = settings ?? [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = accountId, Extra = extra ?? [] },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(string body, string type) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static Task<IReadOnlyList<AppItem>> Load(ProviderCtx c, string view, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Views.Single(v => v.Id == view).Load(c, input.ToDictionary(i => i.Item1, i => i.Item2));
    private static Task<ActionResult> Run(ProviderCtx c, string action, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Actions.Single(a => a.Id == action).Run(c, input.ToDictionary(i => i.Item1, i => i.Item2));

    // ---- signing ---------------------------------------------------------------------------------

    [Fact]
    public void The_AWS_signer_matches_Amazons_published_get_vanilla_test_vector()
    {
        // From the AWS Signature Version 4 test suite: GET / with host and x-amz-date, service "service", region us-east-1.
        var auth = AwsSigV4.Authorization("GET", "/", [], new Dictionary<string, string> { ["host"] = "example.amazonaws.com", ["x-amz-date"] = "20150830T123600Z" }, "",
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service", new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", auth);
    }

    [Fact]
    public void Query_values_are_sorted_and_encoded_before_signing()
    {
        string Sign(params (string, string)[] q) => AwsSigV4.Authorization("GET", "/", q.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)), new Dictionary<string, string> { ["host"] = "h", ["x-amz-date"] = "20150830T123600Z" }, "", "A", "s", "us-east-1", "svc", new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));
        Assert.Equal(Sign(("b", "2"), ("a", "1")), Sign(("a", "1"), ("b", "2"))); // order of the query does not matter
        Assert.NotEqual(Sign(("a", "x y")), Sign(("a", "x+y")));
    }

    [Fact]
    public void The_Tuya_signature_is_capital_hex_and_depends_on_every_part()
    {
        string Sign(string? token = "T", string path = "/v1.0/devices", string body = "") => ProviderApps.TuyaSign("cid", "secret", token, "1700000000000", "nonce", "GET", path, body);
        Assert.Matches("^[0-9A-F]{64}$", Sign());
        Assert.NotEqual(Sign(), Sign(token: null));
        Assert.NotEqual(Sign(), Sign(path: "/v1.0/other"));
        Assert.NotEqual(Sign(), Sign(body: "{\"a\":1}"));
        Assert.Equal(Sign(), Sign());
    }

    // ---- AWS -------------------------------------------------------------------------------------

    [Fact]
    public async Task AWS_lists_servers_from_signed_xml_requests()
    {
        var xml = """<DescribeInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/"><reservationSet><item><instancesSet><item><instanceId>i-0abc</instanceId><instanceType>t3.micro</instanceType><privateIpAddress>10.0.0.4</privateIpAddress><instanceState><name>running</name></instanceState><tagSet><item><key>Name</key><value>web-1</value></item></tagSet></item></instancesSet></item></reservationSet></DescribeInstancesResponse>""";
        var h = new Handler(_ => Text(xml, "text/xml"));
        var items = await Load(Ctx("aws", h, cred: new() { ["accessKeyId"] = "AKIAEXAMPLE12345678", ["secretAccessKey"] = "sec", ["region"] = "ap-south-1" }), "instances");
        var item = Assert.Single(items);
        Assert.Equal("web-1", item.Title); Assert.Equal("running", item.Badge); Assert.Contains("t3.micro", item.Subtitle);
        var req = h.Seen[0].Request;
        Assert.Equal("ec2.ap-south-1.amazonaws.com", req.RequestUri!.Host);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE12345678/", req.Headers.GetValues("Authorization").Single());
        Assert.Contains("Action=DescribeInstances", req.RequestUri.Query);
    }

    [Fact]
    public async Task AWS_credentials_are_checked_before_they_are_saved()
    {
        var prepare = ProviderApps.Get("aws").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["accessKeyId"] = "not-a-key", ["region"] = "ap-south-1" }, CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["accessKeyId"] = "AKIAEXAMPLE12345678", ["region"] = "evil.com/x" }, CancellationToken.None));
        await prepare(new Dictionary<string, string> { ["accessKeyId"] = "AKIAEXAMPLE12345678", ["region"] = "ap-south-1" }, CancellationToken.None);
    }

    // ---- Kubernetes, 1Password, Obsidian, MCP ------------------------------------------------------

    [Fact]
    public async Task Kubernetes_validates_names_before_it_calls_anything()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("kubernetes", h, cred: new() { ["url"] = "https://k8s.example.com:6443", ["token"] = "t" });
        await Assert.ThrowsAsync<AppException>(() => Load(c, "pods", ("namespace", "../kube-system")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "scale", ("namespace", "default"), ("name", "web"), ("replicas", "5000")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "scale", ("namespace", "default"), ("name", "Web;rm"), ("replicas", "2")));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Kubernetes_rejects_something_that_is_not_a_pem_certificate()
    {
        var prepare = ProviderApps.Get("kubernetes").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["url"] = "https://k8s.example.com", ["token"] = "t", ["ca"] = "-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----" }, CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["url"] = "http://k8s.example.com", ["token"] = "t", ["ca"] = "" }, CancellationToken.None)); // https only
    }

    [Fact]
    public async Task The_pods_dashboard_view_works_with_no_input_using_the_default_namespace()
    {
        // The dashboard tile calls a view with no inputs at all, so views must cope with that.
        var h = new Handler(_ => Json("""{"items":[{"metadata":{"name":"web-1"},"spec":{"nodeName":"n1"},"status":{"phase":"Running","containerStatuses":[{"restartCount":2}]}}]}"""));
        var items = await ProviderApps.Get("kubernetes").Views.Single(v => v.Id == "pods").Load(Ctx("kubernetes", h, cred: new() { ["url"] = "https://k8s.example.com", ["token"] = "t" }), new Dictionary<string, string>());
        Assert.Contains("/namespaces/default/pods", h.Seen[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("2 restarts", Assert.Single(items).Subtitle);
    }

    [Fact]
    public async Task Every_dashboard_view_works_when_called_with_no_inputs()
    {
        foreach (var app in ProviderApps.All)
            foreach (var view in app.Views.Where(v => v.Dashboard))
            {
                // Each dashboard list must not index into an empty input dictionary. A failure from a missing key shows up as KeyNotFoundException.
                var ctx = Ctx(app.Key, new Handler(_ => Json("{}")), cred: new() { ["url"] = "https://x.example.com", ["token"] = "t", ["key"] = "k", ["site"] = "x.atlassian.net", ["email"] = "a@b.co", ["username"] = "u", ["password"] = "p", ["accessKeyId"] = "AK", ["secretAccessKey"] = "s", ["region"] = "ap-south-1", ["instance"] = "https://m.example.com", ["handle"] = "h", ["pds"] = "https://bsky.social", ["bridge"] = "10.0.0.2", ["appKey"] = "k", ["clientId"] = "c", ["secret"] = "s", ["env"] = "test", ["mode"] = "live", ["keyId"] = "rzp_test_abcdefgh12", ["keySecret"] = "s", ["apiKey"] = "a", ["userId"] = "u", ["appleId"] = "me@icloud.com", ["accountId"] = new string('a', 32), ["gatewayId"] = "gw", ["host"] = "irc.example.com", ["port"] = "6697", ["nick"] = "daksha", ["webhook"] = "https://nas.example.com/webapi/entry.cgi?api=SYNO.Chat.External&method=incoming", ["homeserver"] = "https://m.example.com", ["number"] = "+919800000000", ["relay"] = "wss://127.0.0.1:9", ["npub"] = "npub1sg6plzptd64u62a878hep2kev88swjh3tw00gjsfl8f237lmu63q0uf63m", ["sid"] = "AC" + new string('a', 32), ["from"] = "+14155550123", ["appId"] = "cli_x", ["appSecret"] = "s", ["domain"] = "feishu" },
                    extra: new() { ["realmId"] = "1", ["shop"] = "x.myshopify.com", ["org"] = "o", ["uri"] = "https://api.calendly.com/users/U", ["email"] = "a@b.co", ["tenantId"] = "t", ["home"] = "https://p1-caldav.icloud.com/1/calendars/", ["selling_partner_id"] = "S", ["username"] = "u", ["open_id"] = "o", ["workspace"] = "1", ["team"] = "1", ["profile"] = "1" }, token: "tok");
                var ex = await Record.ExceptionAsync(() => view.Load(ctx, new Dictionary<string, string>()));
                Assert.False(ex is KeyNotFoundException or NullReferenceException or InvalidOperationException { Message: "Sequence contains no elements" }, $"{app.Key}/{view.Id}: {ex?.GetType().Name}: {ex?.Message}");
            }
    }

    [Fact]
    public async Task OnePassword_lists_titles_only_and_never_asks_for_fields()
    {
        var h = new Handler(r => Json("""[{"id":"abcdefghijklmnopqrstuvwxyz","title":"Bank login","category":"LOGIN","updatedAt":"2026-12-01T00:00:00Z","fields":[{"value":"SECRET"}]}]"""));
        var items = await Load(Ctx("1password", h, cred: new() { ["url"] = "https://connect.example.com", ["token"] = "t" }), "items", ("arg", "abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal("Bank login", Assert.Single(items).Title);
        Assert.DoesNotContain("SECRET", string.Concat(items.Select(i => i.Title + i.Subtitle + i.Body)));
        Assert.EndsWith("/items", h.Seen[0].Request.RequestUri!.AbsolutePath); // the list, never an individual item
        await Assert.ThrowsAsync<AppException>(() => Load(Ctx("1password", h, cred: new() { ["url"] = "https://c", ["token"] = "t" }), "items", ("arg", "../../x")));
    }

    [Fact]
    public async Task Obsidian_paths_cannot_leave_the_vault_and_appends_are_markdown()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("obsidian", h, cred: new() { ["url"] = "http://127.0.0.1:27123", ["key"] = "K" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "append", ("path", "../secrets.md"), ("text", "x")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "append", ("path", "a\\b.md"), ("text", "x")));
        await Run(c, "append", ("path", "Inbox/My idea.md"), ("text", "Buy milk"));
        Assert.EndsWith("/vault/Inbox/My%20idea.md", h.Seen[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("text/markdown", h.Seen[0].Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer", h.Seen[0].Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task MCP_lists_tools_over_a_server_sent_event_reply_and_keeps_the_session()
    {
        var step = 0;
        var h = new Handler(r =>
        {
            step++;
            if (step == 1) { var res = Json("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26"}}"""); res.Headers.Add("Mcp-Session-Id", "sess-1"); return res; }
            if (step == 2) return new HttpResponseMessage(HttpStatusCode.Accepted);
            return Text("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Find things\",\"inputSchema\":{\"properties\":{\"q\":{\"type\":\"string\"}}}}]}}\n\n", "text/event-stream");
        });
        var items = await Load(Ctx("mcp-servers", h, cred: new() { ["url"] = "https://mcp.example.com/mcp", ["token"] = "T" }), "tools");
        Assert.Equal("search", Assert.Single(items).Title);
        Assert.Contains("Arguments", items[0].Body);
        Assert.Equal("sess-1", h.Seen[2].Request.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Contains("\"method\":\"tools/list\"", h.Seen[2].Body);
        Assert.Equal("T", h.Seen[0].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task MCP_tool_errors_and_bad_arguments_are_reported()
    {
        var step = 0;
        var h = new Handler(_ => step++ switch { 0 => Json("""{"jsonrpc":"2.0","id":1,"result":{}}"""), 1 => new HttpResponseMessage(HttpStatusCode.Accepted), _ => Json("""{"jsonrpc":"2.0","id":2,"result":{"isError":true,"content":[{"type":"text","text":"boom"}]}}""") });
        var c = Ctx("mcp-servers", h, cred: new() { ["url"] = "https://mcp.example.com/mcp" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "call", ("tool", "x"), ("arguments", "not json")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "call", ("tool", "x"), ("arguments", "[1]")));
        Assert.Empty(h.Seen);
        var ex = await Assert.ThrowsAsync<AppException>(() => Run(c, "call", ("tool", "x"), ("arguments", "{}")));
        Assert.Contains("boom", ex.Message);
    }

    // ---- stores, books and travel ---------------------------------------------------------------

    [Fact]
    public async Task WooCommerce_uses_basic_auth_over_https_only()
    {
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("woocommerce").Credentials!.Prepare!(new Dictionary<string, string> { ["url"] = "http://shop.example.com" }, CancellationToken.None));
        var h = new Handler(_ => Json("""[{"id":55,"number":"55","status":"processing","total":"499.00","currency":"INR","billing":{"first_name":"Asha","last_name":"R"},"date_created":"2026-12-01T00:00:00"}]"""));
        var items = await Load(Ctx("woocommerce", h, cred: new() { ["url"] = "https://shop.example.com", ["key"] = "ck_1", ["secret"] = "cs_1" }), "orders");
        Assert.Equal("#55 · 499.00 INR", Assert.Single(items).Title);
        Assert.Equal("Basic", h.Seen[0].Request.Headers.Authorization!.Scheme);
        await Assert.ThrowsAsync<AppException>(() => Run(Ctx("woocommerce", h, cred: new() { ["url"] = "https://shop.example.com", ["key"] = "k", ["secret"] = "s" }), "note", ("order", "5/../9"), ("note", "x")));
    }

    [Fact]
    public async Task Xero_sends_the_tenant_header_and_reads_the_json_date_strings()
    {
        var h = new Handler(_ => Json("""{"Invoices":[{"InvoiceID":"i1","InvoiceNumber":"INV-1","Contact":{"Name":"Acme"},"Total":120.5,"CurrencyCode":"AUD","DueDateString":"2026-12-10T00:00:00","Status":"AUTHORISED"}]}"""));
        var items = await Load(Ctx("xero", h, extra: new() { ["tenantId"] = "TEN" }, token: "T"), "invoices");
        Assert.Equal("TEN", h.Seen[0].Request.Headers.GetValues("xero-tenant-id").Single());
        Assert.Equal("INV-1 · Acme", Assert.Single(items).Title); Assert.Contains("10 Dec 2026", items[0].Subtitle);
    }

    [Fact]
    public void Zoho_Books_region_and_eBay_needs_its_RuName()
    {
        var zb = ProviderApps.Get("zoho-books");
        Assert.Equal("https://accounts.zoho.in/oauth/v2/token", ProviderEngine.RegionUrl(zb.OAuth!, zb.OAuth!.TokenUrl, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZohoBooks:Region"] = "in" }).Build()));
        var ebay = ProviderApps.Get("ebay");
        Assert.False(ebay.IsConfigured(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EBay:OAuth:ClientId"] = "a", ["EBay:OAuth:ClientSecret"] = "b" }).Build()));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EBay:OAuth:ClientId"] = "a", ["EBay:OAuth:ClientSecret"] = "b", ["EBay:RuName"] = "My-RuName-123" }).Build();
        Assert.True(ebay.IsConfigured(cfg));
        var url = ProviderEngine.BuildAuthorizationUrl(ebay, cfg, new OAuthConfig("a", "b", "https://api/cb", "https://app"), "st", null, false, new Dictionary<string, string>());
        Assert.Contains("redirect_uri=My-RuName-123", url);
        Assert.StartsWith("https://auth.ebay.com/oauth2/authorize", url);
    }

    [Fact]
    public async Task Mapbox_geocodes_both_ends_then_routes_and_keeps_the_token_server_side()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.Contains("/geocode/") ? Json("""{"features":[{"properties":{"full_address":"Somewhere, India"},"geometry":{"coordinates":[72.87,19.07]}}]}""") : Json("""{"routes":[{"duration":1500,"distance":12400}]}"""));
        var items = await ProviderApps.Get("mapbox").Views.Single(v => v.Id == "directions").Load(Ctx("mapbox", h, config: new() { ["Mapbox:AccessToken"] = "pk.TOKEN" }), new Dictionary<string, string> { ["from"] = "Bandra", ["to"] = "Dadar", ["mode"] = "driving" });
        Assert.Equal("25 min · 12.4 km", Assert.Single(items).Title);
        Assert.Equal(3, h.Seen.Count);
        Assert.Contains("72.87,19.07;72.87,19.07", h.Seen[2].Request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("pk.TOKEN", items[0].Url); // the token never reaches a link shown to the person
    }

    [Fact]
    public async Task Amadeus_checks_airport_codes_and_formats_durations()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.Contains("oauth2/token") ? Json("""{"access_token":"AT"}""") : Json("""{"data":[{"id":"1","price":{"grandTotal":"5400.00","currency":"INR"},"validatingAirlineCodes":["AI"],"itineraries":[{"duration":"PT2H15M","segments":[{"departure":{"at":"2026-12-05T06:30:00"}}]}]}]}"""));
        var c = Ctx("amadeus", h, cred: new() { ["clientId"] = "cid", ["secret"] = "s", ["env"] = "test" });
        await Assert.ThrowsAsync<AppException>(() => Load(c, "flights", ("from", "BOMBAY"), ("to", "DEL"), ("date", "2026-12-05"), ("adults", "1")));
        var items = await Load(c, "flights", ("from", "bom"), ("to", "del"), ("date", "2026-12-05"), ("adults", "2"));
        Assert.Equal("5400.00 INR · AI", Assert.Single(items).Title); Assert.StartsWith("2h 15m", items[0].Subtitle);
        Assert.Contains("originLocationCode=BOM", h.Seen[1].Request.RequestUri!.Query);
        Assert.Equal("test.api.amadeus.com", h.Seen[1].Request.RequestUri!.Host);
    }

    // ---- social and automation ------------------------------------------------------------------

    [Fact]
    public async Task Mastodon_strips_html_from_posts_and_refuses_http_instances()
    {
        var h = new Handler(_ => Json("""[{"id":"1","content":"<p>Hello <b>world</b><br>again</p>","account":{"acct":"amit"},"created_at":"2026-12-01T00:00:00Z","favourites_count":3,"url":"https://m.example/1"}]"""));
        var items = await Load(Ctx("mastodon", h, cred: new() { ["instance"] = "https://mastodon.example", ["token"] = "T" }), "home");
        Assert.Equal("Hello world\nagain", items[0].Title);
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("mastodon").Credentials!.Prepare!(new Dictionary<string, string> { ["instance"] = "http://mastodon.example", ["token"] = "T" }, CancellationToken.None));
    }

    [Fact]
    public async Task Bluesky_signs_in_with_an_app_password_then_posts_a_record_with_its_did()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("createSession") ? Json("""{"accessJwt":"JWT","did":"did:plc:abc"}""") : Json("""{"uri":"at://did:plc:abc/app.bsky.feed.post/3kxyz"}"""));
        var c = Ctx("bluesky", h, cred: new() { ["handle"] = "me.bsky.social", ["password"] = "app-pw", ["pds"] = "https://bsky.social" });
        var r = await Run(c, "post", ("text", "hello"));
        Assert.Equal("https://bsky.app/profile/me.bsky.social/post/3kxyz", r.Url);
        using var body = JsonDocument.Parse(h.Seen[1].Body);
        Assert.Equal("did:plc:abc", body.RootElement.GetProperty("repo").GetString());
        Assert.Equal("app.bsky.feed.post", body.RootElement.GetProperty("collection").GetString());
        Assert.Equal("JWT", h.Seen[1].Request.Headers.Authorization!.Parameter);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "post", ("text", new string('x', 301))));
    }

    [Theory]
    [InlineData("zapier", "https://hooks.zapier.com/hooks/catch/1/abc/", true)]
    [InlineData("zapier", "https://evil.example.com/hooks.zapier.com", false)]
    [InlineData("zapier", "http://hooks.zapier.com/hooks/catch/1/abc/", false)]
    [InlineData("make", "https://hook.eu1.make.com/abcdef", true)]
    [InlineData("make", "https://hook.eu1.make.com.evil.io/abc", false)]
    public async Task Automation_hooks_only_accept_their_own_services(string key, string url, bool ok)
    {
        var prepare = ProviderApps.Get(key).Credentials!.Prepare!;
        var v = new Dictionary<string, string> { ["name"] = "Main", ["url"] = url };
        if (ok) await prepare(v, CancellationToken.None); else await Assert.ThrowsAsync<AppException>(() => prepare(v, CancellationToken.None));
    }

    [Fact]
    public async Task A_hook_added_in_preferences_is_only_called_if_it_is_on_the_right_host()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("zapier", h, cred: new() { ["name"] = "Main", ["url"] = "https://hooks.zapier.com/hooks/catch/1/a/" }, settings: new() { ["hooks"] = "Report=https://hooks.zapier.com/hooks/catch/2/b/; Bad=https://evil.example.com/x" });
        var items = await Load(c, "hooks");
        Assert.Equal(["Main", "Report", "Bad"], items.Select(i => i.Title));
        await Run(c, "trigger", ("hook", "Report"), ("message", "hi"));
        Assert.Equal("hooks.zapier.com", h.Seen[0].Request.RequestUri!.Host);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "trigger", ("hook", "Bad"), ("message", "hi")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "trigger", ("hook", "Nope"), ("message", "hi")));
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task IFTTT_event_names_are_validated_and_the_key_stays_in_the_path()
    {
        var h = new Handler(_ => Text("Congratulations!", "text/plain"));
        var c = Ctx("ifttt", h, cred: new() { ["key"] = "abcdefghij1234" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "fire", ("event", "../x"), ("value1", ""), ("value2", ""), ("value3", "")));
        await Run(c, "fire", ("event", "door_open"), ("value1", "front"), ("value2", ""), ("value3", ""));
        Assert.Equal("/trigger/door_open/with/key/abcdefghij1234", h.Seen[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("front", h.Seen[0].Body);
    }

    // ---- the home -------------------------------------------------------------------------------

    [Fact]
    public async Task Hue_lists_lights_keyed_by_id_and_reports_bridge_errors()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"1":{"name":"Desk","state":{"on":true,"bri":127,"reachable":true}},"2":{"name":"Hall","state":{"on":false,"reachable":false}}}""") : Json("""[{"error":{"type":3,"description":"resource, /lights/9, not available"}}]"""));
        var c = Ctx("philips-hue", h, cred: new() { ["bridge"] = "192.168.1.20", ["appKey"] = "KEY" });
        var items = await Load(c, "lights");
        Assert.Equal(["Desk", "Hall"], items.Select(i => i.Title)); Assert.Equal("on", items[0].Badge); Assert.Equal("Brightness 50%", items[0].Subtitle); Assert.Equal("Not reachable", items[1].Subtitle);
        Assert.Equal("http://192.168.1.20/api/KEY/lights", h.Seen[0].Request.RequestUri!.ToString());
        var ex = await Assert.ThrowsAsync<AppException>(() => Run(c, "light", ("light", "9"), ("power", "on"), ("brightness", "")));
        Assert.Contains("not available", ex.Message);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "light", ("light", "1"), ("power", "on"), ("brightness", "500")));
    }

    [Fact]
    public async Task Hue_pairing_tells_you_to_press_the_button_and_never_targets_link_local_hosts()
    {
        var prepare = ProviderApps.Get("philips-hue").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["bridge"] = "169.254.169.254", ["appKey"] = "" }, CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["bridge"] = "bad host!", ["appKey"] = "" }, CancellationToken.None));
        var v = new Dictionary<string, string> { ["bridge"] = "http://192.168.1.20/", ["appKey"] = "EXISTING" };
        await prepare(v, CancellationToken.None); // a key that was supplied is kept without calling the bridge
        Assert.Equal("192.168.1.20", v["bridge"]);
    }

    [Fact]
    public async Task SmartThings_commands_need_a_real_device_id()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("smartthings", h, cred: new() { ["token"] = "T" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "command", ("device", "abc"), ("command", "on")));
        await Run(c, "command", ("device", "11111111-2222-3333-4444-555555555555"), ("command", "off"));
        Assert.Contains("\"command\":\"off\"", h.Seen[0].Body);
        Assert.Equal("T", h.Seen[0].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Sonos_commands_check_the_room_and_the_volume()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("sonos", h, token: "T", accountId: "Sonos_house");
        await Assert.ThrowsAsync<AppException>(() => Run(c, "volume", ("group", "RINCON_1:2"), ("volume", "101")));
        await Run(c, "volume", ("group", "RINCON_1:2"), ("volume", "35"));
        Assert.EndsWith("/groups/RINCON_1%3A2/groupVolume", h.Seen[0].Request.RequestUri!.AbsolutePath.Length > 0 ? h.Seen[0].Request.RequestUri!.AbsoluteUri.Split('?')[0] : "");
        Assert.Contains("\"volume\":35", h.Seen[0].Body);
    }

    // ---- utilities ------------------------------------------------------------------------------

    [Fact]
    public async Task Weather_looks_up_each_place_then_reads_its_forecast_with_no_key()
    {
        var h = new Handler(r => r.RequestUri!.Host.StartsWith("geocoding") ? Json("""{"results":[{"name":"Mumbai","admin1":"Maharashtra","latitude":19.07,"longitude":72.87}]}""")
            : Json("""{"current":{"temperature_2m":31.2,"apparent_temperature":35.0,"weather_code":2,"wind_speed_10m":11.5},"daily":{"time":["2026-12-01","2026-12-02","2026-12-03","2026-12-04"],"weather_code":[2,61,0,3],"temperature_2m_max":[33,32,31,30],"temperature_2m_min":[24,23,22,21],"precipitation_probability_max":[10,60,5,20]}}"""));
        var items = await Load(Ctx("weather", h, settings: new() { ["places"] = "Mumbai" }), "now");
        var item = Assert.Single(items);
        Assert.Equal("Mumbai, Maharashtra: 31.2°C, Partly cloudy", item.Title); Assert.Equal("33° / 24°", item.Badge);
        Assert.Null(h.Seen[0].Request.Headers.Authorization);
        var days = await Load(Ctx("weather", h), "forecast", ("place", "Mumbai"));
        Assert.Equal(4, days.Count); Assert.Contains("Light rain", days[1].Title); Assert.Contains("60%", days[1].Subtitle);
    }

    [Fact]
    public async Task Weather_says_so_when_a_place_does_not_exist()
    {
        var h = new Handler(_ => Json("""{"generationtime_ms":0.1}"""));
        var ex = await Assert.ThrowsAsync<AppException>(() => Load(Ctx("weather", h), "forecast", ("place", "Nowhereville")));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Weather_needs_no_server_key_and_no_sign_in()
    {
        var w = ProviderApps.Get("weather");
        Assert.Equal(AuthKind.Open, w.Auth);
        Assert.True(w.IsConfigured(new ConfigurationBuilder().Build()));
        Assert.Equal("open", ProviderEngine.ToSpec(w).Auth);
    }

    [Fact]
    public async Task Bhashini_looks_up_the_model_then_runs_it_with_the_key_it_was_given()
    {
        var h = new Handler(r => r.RequestUri!.Host == "meity-auth.ulcacontrib.org"
            ? Json("""{"pipelineResponseConfig":[{"config":[{"serviceId":"ai4bharat/indictrans"}]}],"pipelineInferenceAPIEndPoint":{"callbackUrl":"https://dhruva-api.bhashini.gov.in/services/inference/pipeline","inferenceApiKey":{"name":"Authorization","value":"INFER"}}}""")
            : Json("""{"pipelineResponse":[{"output":[{"target":"नमस्ते"}]}]}"""));
        var items = await Load(Ctx("bhashini", h, cred: new() { ["userId"] = "U", ["apiKey"] = "K" }), "translate", ("text", "Hello"), ("from", "en"), ("to", "hi"));
        Assert.Equal("नमस्ते", Assert.Single(items).Title);
        Assert.Equal("U", h.Seen[0].Request.Headers.GetValues("userID").Single());
        Assert.Equal("INFER", h.Seen[1].Request.Headers.GetValues("Authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => Load(Ctx("bhashini", h, cred: new() { ["userId"] = "U", ["apiKey"] = "K" }), "translate", ("text", "x"), ("from", "en"), ("to", "xx")));
        var hint = await Load(Ctx("bhashini", h, cred: new() { ["userId"] = "U", ["apiKey"] = "K" }), "translate");
        Assert.Contains("Type some text", Assert.Single(hint).Title);
    }
}
