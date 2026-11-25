using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace Daksh.Api.Tests;

public class ProviderAppsTests
{
    private static readonly string[] Expected =
    [
        "youtube", "google-maps-platform", "uber", "yahoo-mail", "proton-mail", "apple-calendar", "instagram", "facebook-pages", "x", "linkedin",
        "tiktok", "reddit", "pinterest", "amazon-selling-partner", "shopify", "booking-com", "quickbooks-online", "home-assistant", "irctc", "digilocker",
    ];

    private static readonly OAuthConfig Cfg = new("cid", "sec", "https://api.example.com/api/integrations/x/callback", "https://app.example.com/apps/x");

    private static string[] Scopes(string url, string param, string separator) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)[param].ToString().Split(separator, StringSplitOptions.RemoveEmptyEntries);

    private static IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => (string?)v.Item2)).Build();

    // ---- the definitions themselves ------------------------------------------------------------

    [Fact]
    public void All_twenty_apps_are_defined_once_with_catalog_keys()
    {
        Assert.Equal(Expected.OrderBy(k => k), ProviderApps.All.Select(a => a.Key).OrderBy(k => k));
        Assert.Equal(20, ProviderApps.All.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void Every_app_is_complete_and_its_views_and_actions_refer_to_real_things()
    {
        foreach (var app in ProviderApps.All)
        {
            var ids = app.Views.Select(v => v.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Contains(app.Views, v => !v.Hidden);
            // The dashboard tile needs at least one list that works with no input.
            Assert.Contains(app.Views, v => v.Dashboard && !v.TakesArg && v.Inputs.All(i => !i.Required));
            Assert.Contains(app.DefaultDashboardView, ids);
            foreach (var v in app.Views) Assert.True(!v.Hidden || v.TakesArg, $"{app.Key}/{v.Id}: a hidden view is only reached by drilling in");
            foreach (var input in app.Views.SelectMany(v => v.Inputs).Concat(app.Actions.SelectMany(a => a.Inputs)))
            {
                if (input.Kind == "select-view") Assert.Contains(input.OptionsView, ids);
                if (input.Kind == "select") Assert.NotEmpty(input.Options!);
            }
            Assert.Equal(app.Actions.Length, app.Actions.Select(a => a.Id).Distinct().Count());
            if (!app.HasWrite) Assert.Empty(app.Actions);
            switch (app.Auth)
            {
                case AuthKind.OAuth: Assert.NotNull(app.OAuth); Assert.NotNull(app.Profile); break;
                case AuthKind.Credentials: Assert.NotNull(app.Credentials); Assert.NotEmpty(app.Credentials!.Fields); break;
                case AuthKind.ServerKey: Assert.NotEmpty(app.ServerKeys); break;
            }
            // Settings the server accepts: the dashboard list must be one of the dashboard views.
            var settings = app.ToInfo().Settings;
            var dash = settings.Single(s => s.Key == "dashboardView");
            Assert.Contains(dash.Default, dash.Options!);
            Assert.All(dash.Options!, o => Assert.Contains(o, ids));
        }
    }

    [Fact]
    public void Spec_describes_the_connect_form_views_and_actions_for_the_web_app()
    {
        var shopify = ProviderEngine.ToSpec(ProviderApps.Get("shopify"));
        Assert.Equal("oauth", shopify.Auth);
        Assert.Contains(shopify.ConnectFields, f => f.Key == "shop");
        var ha = ProviderEngine.ToSpec(ProviderApps.Get("home-assistant"));
        Assert.Equal("credentials", ha.Auth);
        Assert.Contains(ha.ConnectFields, f => f.Key == "token" && f.Kind == "secret");
        Assert.Equal("serverkey", ProviderEngine.ToSpec(ProviderApps.Get("google-maps-platform")).Auth);
    }

    [Fact]
    public void Read_only_apps_do_not_offer_full_access()
    {
        foreach (var key in new[] { "amazon-selling-partner", "digilocker", "irctc", "booking-com", "google-maps-platform" }) Assert.False(ProviderApps.Get(key).HasWrite, key);
        foreach (var key in new[] { "youtube", "instagram", "x", "shopify", "home-assistant" }) Assert.True(ProviderApps.Get(key).HasWrite, key);
    }

    // ---- sign-in urls --------------------------------------------------------------------------

    [Fact]
    public void Sign_in_is_read_only_until_full_access_and_never_carries_the_secret()
    {
        foreach (var app in ProviderApps.All.Where(a => a.Auth == AuthKind.OAuth && a.OAuth!.BuildAuthUrl is null && a.OAuth.WriteScopes.Length > 0))
        {
            var read = ProviderEngine.BuildAuthorizationUrl(app, Config(), Cfg, "st8", app.OAuth!.Pkce ? "chal" : null, false, new Dictionary<string, string>());
            var write = ProviderEngine.BuildAuthorizationUrl(app, Config(), Cfg, "st8", app.OAuth.Pkce ? "chal" : null, true, new Dictionary<string, string>());
            Assert.Contains("state=st8", read);
            Assert.DoesNotContain("sec", read.Replace("scope", "").Replace("response", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("client_secret", read);
            var readScopes = Scopes(read, app.OAuth.ScopeParam, app.OAuth.ScopeSeparator); var writeScopes = Scopes(write, app.OAuth.ScopeParam, app.OAuth.ScopeSeparator);
            foreach (var scope in app.OAuth.WriteScopes) { Assert.DoesNotContain(scope, readScopes); Assert.Contains(scope, writeScopes); }
            foreach (var scope in app.OAuth.ReadScopes) Assert.Contains(scope, readScopes);
        }
    }

    [Fact]
    public void YouTube_asks_for_offline_access_with_its_own_consent()
    {
        var url = ProviderEngine.BuildAuthorizationUrl(ProviderApps.Get("youtube"), Config(), Cfg, "s", null, false, new Dictionary<string, string>());
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("youtube.readonly", url);
        Assert.DoesNotContain("gmail", url);
    }

    [Fact]
    public void X_uses_PKCE_and_TikTok_names_its_client_key()
    {
        var x = ProviderEngine.BuildAuthorizationUrl(ProviderApps.Get("x"), Config(), Cfg, "s", "chal", false, new Dictionary<string, string>());
        Assert.Contains("code_challenge=chal", x); Assert.Contains("code_challenge_method=S256", x);
        var t = ProviderEngine.BuildAuthorizationUrl(ProviderApps.Get("tiktok"), Config(), Cfg, "s", null, false, new Dictionary<string, string>());
        Assert.Contains("client_key=cid", t); Assert.DoesNotContain("client_id=", t);
        Assert.Equal(["user.info.basic", "user.info.stats", "video.list"], Scopes(t, "scope", ","));
    }

    [Fact]
    public void Shopify_sign_in_goes_to_the_normalised_shop_and_rejects_other_hosts()
    {
        var shopify = ProviderApps.Get("shopify");
        var url = ProviderEngine.BuildAuthorizationUrl(shopify, Config(), Cfg, "s", null, true, new Dictionary<string, string> { ["shop"] = "https://My-Store/" });
        Assert.StartsWith("https://my-store.myshopify.com/admin/oauth/authorize?", url);
        Assert.Contains("write_products", url);
        Assert.Throws<AppException>(() => ProviderEngine.BuildAuthorizationUrl(shopify, Config(), Cfg, "s", null, false, new Dictionary<string, string> { ["shop"] = "evil.com/x" }));
        Assert.Throws<AppException>(() => ProviderApps.NormalizeShop("my-store.example.com"));
    }

    [Fact]
    public void Shopify_callback_must_carry_a_valid_signature()
    {
        var check = ProviderApps.Get("shopify").OAuth!.ValidateCallback!;
        var q = new Dictionary<string, string> { ["code"] = "abc", ["shop"] = "my-store.myshopify.com", ["state"] = "s", ["timestamp"] = "1700000000" };
        var message = string.Join('&', q.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        q["hmac"] = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("sec"), Encoding.UTF8.GetBytes(message)));
        Assert.Null(check(Cfg, q));
        q["code"] = "tampered";
        Assert.NotNull(check(Cfg, q));
        q["code"] = "abc"; q["shop"] = "evil.example.com";
        Assert.NotNull(check(Cfg, q));
    }

    [Fact]
    public void Amazon_sends_the_seller_to_seller_central_and_needs_an_application_id()
    {
        var amazon = ProviderApps.Get("amazon-selling-partner");
        var cfg = Config(("AmazonSellingPartner:ApplicationId", "amzn1.sp.solution.1"));
        var url = ProviderEngine.BuildAuthorizationUrl(amazon, cfg, Cfg, "s", null, false, new Dictionary<string, string>());
        Assert.StartsWith("https://sellercentral.amazon.in/apps/authorize/consent?", url);
        Assert.Contains("application_id=amzn1.sp.solution.1", url);
        Assert.Contains("version=beta", url);
        Assert.False(amazon.IsConfigured(Config(("AmazonSellingPartner:OAuth:ClientId", "a"), ("AmazonSellingPartner:OAuth:ClientSecret", "b"))));
        Assert.True(amazon.IsConfigured(Config(("AmazonSellingPartner:OAuth:ClientId", "a"), ("AmazonSellingPartner:OAuth:ClientSecret", "b"), ("AmazonSellingPartner:ApplicationId", "x"))));
    }

    [Fact]
    public void YouTube_can_borrow_the_Google_client_but_other_apps_cannot()
    {
        var gmail = Config(("Gmail:OAuth:ClientId", "g"), ("Gmail:OAuth:ClientSecret", "s"));
        Assert.True(ProviderApps.Get("youtube").IsConfigured(gmail));
        Assert.False(ProviderApps.Get("x").IsConfigured(gmail));
        Assert.False(ProviderApps.Get("google-maps-platform").IsConfigured(Config()));
        Assert.True(ProviderApps.Get("google-maps-platform").IsConfigured(Config(("GoogleMaps:ApiKey", "k"))));
        Assert.True(ProviderApps.Get("home-assistant").IsConfigured(Config())); // nothing to set up on the server
    }

    // ---- reading provider replies --------------------------------------------------------------

    [Fact]
    public void Json_paths_templates_and_dates_are_forgiving()
    {
        using var doc = JsonDocument.Parse("""{"a":{"b":[{"c":"x","n":5,"t":true}]},"ts":"1700000000","iso":"2026-12-05T10:00:00Z"}""");
        var r = doc.RootElement;
        Assert.Equal("x", J.S(r, "a.b[0].c")); Assert.Equal("5", J.S(r, "a.b[0].n")); Assert.Equal("true", J.S(r, "a.b[0].t"));
        Assert.Null(J.S(r, "a.b[3].c")); Assert.Null(J.S(r, "nope.deeper"));
        Assert.Equal("x", J.First(r, "missing", "a.b[0].c"));
        Assert.Equal("x · 5", J.T(r, "{a.b[0].c} · {a.b[0].n}"));
        Assert.Equal("x", J.T(r, "{a.b[0].c} · {nope}"));
        Assert.Equal("5 Dec 2026", J.When("2026-12-05T10:00:00Z"));
        Assert.Equal("14 Nov 2023", J.When("1700000000"));
        Assert.Equal("", J.When("garbage"));
    }

    [Fact]
    public void Error_details_come_from_whichever_field_the_provider_uses()
    {
        Assert.Equal("Quota exceeded", ProviderEngine.ErrorDetail("""{"error":{"message":"Quota exceeded"}}"""));
        Assert.Equal("bad", ProviderEngine.ErrorDetail("""{"message":"bad"}"""));
        Assert.Equal("nope", ProviderEngine.ErrorDetail("""{"errors":[{"message":"nope"}]}"""));
        Assert.Null(ProviderEngine.ErrorDetail("<html>oops</html>"));
        Assert.Null(ProviderEngine.ErrorDetail(""));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Seen.Add(request); return Task.FromResult(respond(request)); }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? extra = null, Dictionary<string, string>? cred = null, Dictionary<string, string>? settings = null, IConfiguration? config = null, string? token = "tok") => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = config ?? Config(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = settings ?? [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = "acct", Extra = extra ?? [] },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task YouTube_playlists_become_rows_that_drill_into_the_playlist()
    {
        var h = new Handler(_ => Json("""{"items":[{"id":"PL1","snippet":{"title":"Focus","thumbnails":{"default":{"url":"https://i/1.jpg"}}},"contentDetails":{"itemCount":12}}]}"""));
        var items = await ProviderApps.Get("youtube").Views.Single(v => v.Id == "playlists").Load(Ctx("youtube", h), new Dictionary<string, string>());
        var item = Assert.Single(items);
        Assert.Equal("Focus", item.Title); Assert.Equal("12 videos", item.Subtitle); Assert.Equal("playlist-items", item.Drill);
        Assert.Equal("https://www.youtube.com/playlist?list=PL1", item.Url);
        Assert.Equal("Bearer", h.Seen[0].Headers.Authorization!.Scheme); Assert.Equal("tok", h.Seen[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task YouTube_search_escapes_what_the_person_typed()
    {
        var h = new Handler(_ => Json("""{"items":[{"id":{"videoId":"abcdefghijk"},"snippet":{"title":"T","channelTitle":"C","publishedAt":"2026-01-02T00:00:00Z"}}]}"""));
        var items = await ProviderApps.Get("youtube").Views.Single(v => v.Id == "search").Load(Ctx("youtube", h), new Dictionary<string, string> { ["q"] = "a&b=c d" });
        Assert.Contains("q=a%26b%3Dc%20d", h.Seen[0].RequestUri!.Query);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", Assert.Single(items).Url);
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=3", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("not a video", null)]
    public void YouTube_video_links_are_recognised(string input, string? id) => Assert.Equal(id, ProviderApps.YoutubeVideoId(input));

    [Fact]
    public async Task Provider_errors_become_friendly_messages_with_a_code()
    {
        async Task<AppException> Fail(HttpStatusCode code, string body = "{}")
        {
            var h = new Handler(_ => Json(body, code));
            return await Assert.ThrowsAsync<AppException>(() => Ctx("x", h).GetAsync("https://api.twitter.com/2/users/me"));
        }
        var expired = await Fail(HttpStatusCode.Unauthorized); Assert.Equal("reconnect_required", expired.Code); Assert.Equal(409, expired.StatusCode);
        var forbidden = await Fail(HttpStatusCode.Forbidden, """{"detail":"Your plan does not include this"}"""); Assert.Equal(403, forbidden.StatusCode); Assert.Contains("Your plan does not include this", forbidden.Message);
        var missing = await Fail(HttpStatusCode.NotFound); Assert.Equal("not_found", missing.Code);
        var rejected = await Fail(HttpStatusCode.BadRequest, """{"message":"Text too long"}"""); Assert.Contains("Text too long", rejected.Message);
        var down = await Fail(HttpStatusCode.InternalServerError); Assert.Equal(502, down.StatusCode);
    }

    [Fact]
    public async Task Shopify_uses_its_own_token_header_instead_of_a_bearer_token()
    {
        var h = new Handler(_ => Json("""{"orders":[{"id":1,"name":"#1001","total_price":"499.00","currency":"INR","financial_status":"paid","customer":{"first_name":"Asha","last_name":"R"},"created_at":"2026-12-01T00:00:00Z"}]}"""));
        var items = await ProviderApps.Get("shopify").Views.Single(v => v.Id == "orders").Load(Ctx("shopify", h, new() { ["shop"] = "my-store.myshopify.com" }), new Dictionary<string, string>());
        Assert.Null(h.Seen[0].Headers.Authorization);
        Assert.Equal("tok", h.Seen[0].Headers.GetValues("X-Shopify-Access-Token").Single());
        Assert.StartsWith("https://my-store.myshopify.com/admin/api/", h.Seen[0].RequestUri!.ToString());
        Assert.Equal("#1001 · 499.00 INR", Assert.Single(items).Title);
    }

    [Fact]
    public async Task Home_assistant_lists_only_controllable_devices_and_sensors_with_units()
    {
        var states = """[{"entity_id":"light.lamp","state":"on","attributes":{"friendly_name":"Lamp"}},{"entity_id":"sensor.temp","state":"24","attributes":{"friendly_name":"Temp","unit_of_measurement":"°C"}},{"entity_id":"person.amit","state":"home","attributes":{}}]""";
        var cred = new Dictionary<string, string> { ["url"] = "http://ha.local:8123", ["token"] = "T" };
        var views = ProviderApps.Get("home-assistant").Views;
        var devices = await views.Single(v => v.Id == "devices").Load(Ctx("home-assistant", new Handler(_ => Json(states)), cred: cred, token: null), new Dictionary<string, string>());
        Assert.Equal(["light.lamp"], devices.Select(d => d.Id)); Assert.Equal("on", devices[0].Badge);
        var h = new Handler(_ => Json(states));
        var sensors = await views.Single(v => v.Id == "sensors").Load(Ctx("home-assistant", h, cred: cred, token: null), new Dictionary<string, string>());
        Assert.Equal("24 °C", Assert.Single(sensors).Badge);
        Assert.Equal("T", h.Seen[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Home_assistant_switches_a_cover_with_the_cover_service_and_a_light_with_the_generic_one()
    {
        var cred = new Dictionary<string, string> { ["url"] = "http://ha.local:8123", ["token"] = "T" };
        var h = new Handler(_ => Json("[]"));
        var action = ProviderApps.Get("home-assistant").Actions.Single(a => a.Id == "switch");
        await action.Run(Ctx("home-assistant", h, cred: cred, token: null), new Dictionary<string, string> { ["entity"] = "cover.garage", ["command"] = "turn_on" });
        await action.Run(Ctx("home-assistant", h, cred: cred, token: null), new Dictionary<string, string> { ["entity"] = "light.lamp", ["command"] = "toggle" });
        Assert.EndsWith("/api/services/cover/open_cover", h.Seen[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/api/services/homeassistant/toggle", h.Seen[1].RequestUri!.AbsolutePath);
        await Assert.ThrowsAsync<AppException>(() => action.Run(Ctx("home-assistant", h, cred: cred, token: null), new Dictionary<string, string> { ["entity"] = "person.amit", ["command"] = "toggle" }));
    }

    [Fact]
    public async Task QuickBooks_invoices_show_paid_and_owed()
    {
        var body = """{"QueryResponse":{"Invoice":[{"Id":"7","DocNumber":"1007","CustomerRef":{"name":"Acme"},"TotalAmt":500,"CurrencyRef":{"value":"INR"},"DueDate":"2026-12-10","Balance":0},{"Id":"8","DocNumber":"1008","CustomerRef":{"name":"Zed"},"TotalAmt":90,"CurrencyRef":{"value":"INR"},"DueDate":"2026-12-11","Balance":90}]}}""";
        var h = new Handler(_ => Json(body));
        var items = await ProviderApps.Get("quickbooks-online").Views.Single(v => v.Id == "invoices").Load(Ctx("quickbooks-online", h, new() { ["realmId"] = "123" }), new Dictionary<string, string>());
        Assert.Equal(["paid", "owes 90"], items.Select(i => i.Badge));
        Assert.Contains("/v3/company/123/query", h.Seen[0].RequestUri!.AbsolutePath);
        Assert.Contains("application/json", h.Seen[0].Headers.Accept.ToString());
    }

    [Fact]
    public async Task X_timeline_names_the_author_of_each_post()
    {
        var body = """{"data":[{"id":"111","text":"hello","author_id":"9","created_at":"2026-12-01T00:00:00Z","public_metrics":{"like_count":3}}],"includes":{"users":[{"id":"9","username":"amit"}]}}""";
        var items = await ProviderApps.Get("x").Views.Single(v => v.Id == "mentions").Load(Ctx("x", new Handler(_ => Json(body))), new Dictionary<string, string>());
        var i = Assert.Single(items);
        Assert.Equal("https://x.com/amit/status/111", i.Url); Assert.Contains("@amit", i.Subtitle);
    }

    [Fact]
    public async Task Reddit_posts_drill_into_their_comments_and_sends_a_descriptive_user_agent()
    {
        var body = """{"data":{"children":[{"kind":"t3","data":{"id":"abc123","title":"Hi","subreddit":"test","author":"me","score":5,"num_comments":2,"permalink":"/r/test/comments/abc123/hi/","thumbnail":"self"}}]}}""";
        var h = new Handler(_ => Json(body));
        var items = await ProviderApps.Get("reddit").Views.Single(v => v.Id == "home").Load(Ctx("reddit", h), new Dictionary<string, string>());
        var i = Assert.Single(items);
        Assert.Equal("thread", i.Drill); Assert.Equal("abc123", i.Id); Assert.Null(i.Image);
        Assert.StartsWith("web:daksha", h.Seen[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Reddit_actions_refuse_ids_that_are_not_reddit_ids()
    {
        var comment = ProviderApps.Get("reddit").Actions.Single(a => a.Id == "comment");
        await Assert.ThrowsAsync<AppException>(() => comment.Run(Ctx("reddit", new Handler(_ => Json("{}"))), new Dictionary<string, string> { ["thing"] = "../../me", ["text"] = "x" }));
    }

    [Fact]
    public async Task Uber_estimates_need_two_proper_coordinates()
    {
        var view = ProviderApps.Get("uber").Views.Single(v => v.Id == "estimates");
        await Assert.ThrowsAsync<AppException>(() => view.Load(Ctx("uber", new Handler(_ => Json("{}"))), new Dictionary<string, string> { ["from"] = "Mumbai", ["to"] = "19,72" }));
        var h = new Handler(_ => Json("""{"prices":[{"product_id":"p1","display_name":"UberGo","estimate":"₹200-250","duration":900,"distance":4.2}]}"""));
        var items = await view.Load(Ctx("uber", h), new Dictionary<string, string> { ["from"] = "19.07, 72.87", ["to"] = "19.11, 72.86" });
        Assert.Equal("15 min · 4.2 mi", Assert.Single(items).Subtitle);
        Assert.Contains("start_latitude=19.07", h.Seen[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Uber_with_no_current_ride_is_an_empty_list_not_an_error()
    {
        var items = await ProviderApps.Get("uber").Views.Single(v => v.Id == "current").Load(Ctx("uber", new Handler(_ => Json("{}", HttpStatusCode.NotFound))), new Dictionary<string, string>());
        Assert.Empty(items);
    }

    [Fact]
    public async Task Maps_commute_is_empty_until_home_and_work_are_set_and_uses_the_server_key()
    {
        var view = ProviderApps.Get("google-maps-platform").Views.Single(v => v.Id == "commute");
        var none = await view.Load(Ctx("google-maps-platform", new Handler(_ => Json("{}")), token: null), new Dictionary<string, string>());
        Assert.Empty(none);
        var h = new Handler(_ => Json("""{"routes":[{"duration":"1500s","distanceMeters":12400,"description":"Western Express Hwy"}]}"""));
        var items = await view.Load(Ctx("google-maps-platform", h, settings: new() { ["home"] = "Bandra", ["work"] = "Nariman Point" }, config: Config(("GoogleMaps:ApiKey", "KEY")), token: null), new Dictionary<string, string>());
        Assert.Equal(2, items.Count);
        Assert.Equal("25 min · 12.4 km", items[0].Title);
        Assert.Equal("KEY", h.Seen[0].Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.Null(h.Seen[0].Headers.Authorization);
    }

    [Fact]
    public async Task IRCTC_my_pnrs_ignores_anything_that_is_not_ten_digits()
    {
        var h = new Handler(_ => Json("""{"status":true,"data":{"Pnr":"2412345678","TrainName":"Rajdhani","TrainNo":"12951","Doj":"01-12-2026","From":"NDLS","To":"BCT","ChartPrepared":false,"PassengerStatus":[{"CurrentStatus":"CNF"}]}}"""));
        var items = await ProviderApps.Get("irctc").Views.Single(v => v.Id == "my-pnrs").Load(Ctx("irctc", h, cred: new() { ["key"] = "K" }, settings: new() { ["pnrs"] = "2412345678, ../etc, 123" }, token: null), new Dictionary<string, string>());
        Assert.Single(h.Seen);
        Assert.Equal("Rajdhani (12951)", Assert.Single(items).Title);
        Assert.Equal("K", h.Seen[0].Headers.GetValues("X-RapidAPI-Key").Single());
    }

    // ---- calendar text -------------------------------------------------------------------------

    [Fact]
    public void Calendar_events_are_read_from_iCalendar_text_in_all_three_time_forms()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:1\r\nDTSTART;TZID=Asia/Kolkata:20261201T093000\r\nDTEND;TZID=Asia/Kolkata:20261201T103000\r\nSUMMARY:Stand\\, up\r\n with team\r\nLOCATION:Room 4\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nUID:2\r\nDTSTART;VALUE=DATE:20261205\r\nSUMMARY:Holiday\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:3\r\nDTSTART:20261206T070000Z\r\nDTEND:20261206T080000Z\r\nSUMMARY:Call\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var events = ProviderApps.ParseEvents(ics, "Home").ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal("Stand, upwith team", events[0].Title); Assert.Equal("Room 4", events[0].Location);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 4, 0, 0, TimeSpan.Zero), events[0].Start.ToUniversalTime());
        Assert.True(events[1].AllDay);
        Assert.Equal(new DateTimeOffset(2026, 12, 6, 7, 0, 0, TimeSpan.Zero), events[2].Start);
    }

    // ---- not letting the server be pointed at itself -------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("ftp://example.com")]
    [InlineData("http://user:pass@192.168.1.5:8123")]
    [InlineData("not a url")]
    [InlineData("http://0.0.0.0:8123")]
    [InlineData("http://[fe80::1]:8123")]
    public async Task Server_side_addresses_that_point_at_metadata_or_are_malformed_are_refused(string url) =>
        await Assert.ThrowsAsync<AppException>(() => HostGuard.CheckAsync(url, allowHttp: true, CancellationToken.None));

    [Theory]
    [InlineData("http://192.168.1.5:8123")]
    [InlineData("http://127.0.0.1:8123")]
    [InlineData("https://203.0.113.9")]
    public async Task Home_networks_and_public_addresses_are_allowed(string url) => Assert.NotNull(await HostGuard.CheckAsync(url, allowHttp: true, CancellationToken.None));

    [Fact]
    public async Task Https_only_callers_refuse_plain_http() =>
        await Assert.ThrowsAsync<AppException>(() => HostGuard.CheckAsync("http://203.0.113.9", allowHttp: false, CancellationToken.None));
}

public class ProviderAppsRoutesTests
{
    /// <summary>Signs in anyone who sends "X-Test-User" (no database or token service needed).</summary>
    private sealed class StubAuth(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> o, Microsoft.Extensions.Logging.ILoggerFactory l, System.Text.Encodings.Web.UrlEncoder e)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
            var id = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.ToString()), new System.Security.Claims.Claim("sub", user.ToString())], Daksh.Api.Auth.AuthSetup.UserScheme);
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(id), Daksh.Api.Auth.AuthSetup.UserScheme)));
        }
    }

    private static async Task<Microsoft.AspNetCore.Builder.WebApplication> StartAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ClaimsPrincipalAccessor>();
        builder.Services.AddSingleton<MongoDB.Driver.IMongoDatabase>(new MongoDB.Driver.MongoClient("mongodb://localhost:1").GetDatabase("unused"));
        builder.Services.AddSingleton<Daksh.Platform.Security.ISecretProtector>(new Daksh.Platform.Security.SecretProtector(new Daksh.Platform.Security.SecretProtectorOptions { Keys = { ["k1"] = Convert.ToBase64String(new byte[32]) } }));
        builder.Services.AddAuthentication(Daksh.Api.Auth.AuthSetup.UserScheme).AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, StubAuth>(Daksh.Api.Auth.AuthSetup.UserScheme, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(Daksh.Api.Auth.AuthSetup.UserScheme, p => p.AddAuthenticationSchemes(Daksh.Api.Auth.AuthSetup.UserScheme).RequireAuthenticatedUser()));
        builder.Services.AddProviderApps();
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapProviderApps();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Every_app_registers_cleanly_serves_its_spec_to_a_signed_in_person_and_nothing_to_strangers()
    {
        await using var app = await StartAsync();
        var anon = app.GetTestClient();
        var signedIn = app.GetTestClient(); signedIn.DefaultRequestHeaders.Add("X-Test-User", "someone");
        foreach (var a in ProviderApps.All)
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/integrations/{a.Key}/spec")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/integrations/{a.Key}/connect", null)).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/integrations/{a.Key}/views/{a.Views[0].Id}")).StatusCode);
            var res = await signedIn.GetAsync($"/api/integrations/{a.Key}/spec");
            Assert.True(res.IsSuccessStatusCode, $"{a.Key}: {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(a.Key, doc.RootElement.GetProperty("key").GetString());
            Assert.Equal(a.Views.Length, doc.RootElement.GetProperty("views").GetArrayLength());
            Assert.Equal(a.Actions.Length, doc.RootElement.GetProperty("actions").GetArrayLength());
            Assert.Equal(a.HasWrite, doc.RootElement.GetProperty("hasWrite").GetBoolean());
        }
    }

    [Fact]
    public async Task Only_sign_in_apps_have_a_public_callback()
    {
        await using var app = await StartAsync();
        var anon = app.GetTestClient();
        foreach (var a in ProviderApps.All)
        {
            var res = await anon.GetAsync($"/api/integrations/{a.Key}/callback?code=abc");
            if (a.Auth == AuthKind.OAuth) Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, res.StatusCode);
            else Assert.True(res.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.MethodNotAllowed, $"{a.Key} should not expose a callback");
        }
    }

    [Fact]
    public async Task The_status_summary_requires_a_signed_in_person()
    {
        await using var app = await StartAsync();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/api/integrations/provider-apps/statuses")).StatusCode);
    }
}
