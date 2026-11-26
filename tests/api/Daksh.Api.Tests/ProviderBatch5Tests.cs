using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ProviderBatch5Tests
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

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? cred = null, Dictionary<string, string>? settings = null, string? token = null, string accountId = "acct", Dictionary<string, string?>? config = null) => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = settings ?? [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = accountId },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<IReadOnlyList<AppItem>> Load(ProviderCtx c, string view, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Views.Single(v => v.Id == view).Load(c, input.ToDictionary(i => i.Item1, i => i.Item2));
    private static Task<ActionResult> Run(ProviderCtx c, string action, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Actions.Single(a => a.Id == action).Run(c, input.ToDictionary(i => i.Item1, i => i.Item2));

    [Fact]
    public async Task Teams_strips_html_reads_user_messages_only_and_escapes_what_is_sent()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"value":[{"id":"1","messageType":"message","body":{"content":"<p>Hi <b>team</b></p>"},"from":{"user":{"displayName":"Asha"}},"createdDateTime":"2026-12-01T10:00:00Z"},{"id":"2","messageType":"systemEventMessage","body":{"content":"joined"}}]}""") : Json("{}"));
        var c = Ctx("microsoft-teams", h, token: "T");
        var items = await Load(c, "messages", ("arg", "19:abc@thread.v2"));
        var m = Assert.Single(items); Assert.Equal("Hi team", m.Title); Assert.Contains("Asha", m.Subtitle);
        Assert.Contains("19%3Aabc%40thread.v2", h.Seen[0].Request.RequestUri!.AbsoluteUri);
        await Run(c, "send", ("chat", "19:abc@thread.v2"), ("text", "<script>x</script> & hi"));
        Assert.Equal("&lt;script&gt;x&lt;/script&gt; &amp; hi", JsonDocument.Parse(h.Seen[1].Body).RootElement.GetProperty("body").GetProperty("content").GetString());
        await Assert.ThrowsAsync<AppException>(() => Load(c, "messages", ("arg", "../me")));
    }

    [Fact]
    public async Task Google_chat_only_accepts_space_names()
    {
        var h = new Handler(_ => Json("""{"messages":[{"name":"spaces/AAA/messages/1","text":"Hello","sender":{"displayName":"Ravi"},"createTime":"2026-12-01T10:00:00Z"}]}"""));
        var c = Ctx("google-chat", h, token: "T");
        Assert.Equal("Hello", (await Load(c, "messages", ("arg", "spaces/AAAAxyz"))).Single().Title);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "messages", ("arg", "spaces/../users/me")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "send", ("space", "evil"), ("text", "x")));
    }

    [Fact]
    public async Task Signal_reads_waiting_messages_and_sends_only_to_real_numbers()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""[{"envelope":{"sourceName":"Meera","sourceNumber":"+911234567890","timestamp":1700000000000,"dataMessage":{"message":"Are you free?"}}},{"envelope":{"timestamp":1700000001000,"typingMessage":{}}}]""") : Json("{}"));
        var c = Ctx("signal", h, cred: new() { ["url"] = "http://localhost:8080", ["number"] = "+919800000000" });
        var items = await Load(c, "inbox");
        Assert.Equal("Are you free?", Assert.Single(items).Title); Assert.Contains("Meera", items[0].Subtitle);
        Assert.Contains("%2B919800000000", h.Seen[0].Request.RequestUri!.AbsoluteUri);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "send", ("to", "12345"), ("text", "x")));
        await Run(c, "send", ("to", "+911234567890"), ("text", "Yes"));
        Assert.Equal("+911234567890", JsonDocument.Parse(h.Seen[1].Body).RootElement.GetProperty("recipients")[0].GetString());
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("signal").Credentials!.Prepare!(new Dictionary<string, string> { ["url"] = "http://localhost:8080", ["number"] = "9800000000" }, CancellationToken.None));
    }

    [Fact]
    public async Task LINE_user_ids_are_checked_and_the_account_view_reports_usage()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/info") ? Json("""{"userId":"U1","displayName":"My shop","basicId":"@shop","chatMode":"bot"}""") : r.RequestUri!.AbsolutePath.EndsWith("/consumption") ? Json("""{"totalUsage":42}""") : Json("{}"));
        var c = Ctx("line", h, cred: new() { ["token"] = "T" });
        Assert.Equal("42 sent this month", (await Load(c, "account")).Single().Badge);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "push", ("to", "someone"), ("text", "x")));
        await Run(c, "push", ("to", "U" + new string('a', 32)), ("text", "Hi"));
        Assert.EndsWith("/message/push", h.Seen.Last().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Messenger_uses_the_page_token_and_checks_conversation_ids()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/me/accounts") ? Json("""{"data":[{"id":"P1","name":"Shop","access_token":"PAGETOKEN"}]}""") : r.RequestUri!.AbsolutePath.EndsWith("/conversations") ? Json("""{"data":[{"id":"t_123","snippet":"Hello?","updated_time":"2026-12-01T00:00:00Z","senders":{"data":[{"name":"Customer"}]}}]}""") : Json("{}"));
        var c = Ctx("facebook-messenger", h, token: "USERTOKEN");
        var items = await Load(c, "conversations");
        Assert.Equal("Customer", Assert.Single(items).Title); Assert.Equal("thread", items[0].Drill);
        Assert.Equal("PAGETOKEN", h.Seen[1].Request.Headers.Authorization!.Parameter);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "thread", ("arg", "../x")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "send", ("recipient", "abc"), ("text", "x")));
    }

    [Fact]
    public async Task BlueBubbles_chat_ids_are_validated_and_the_password_stays_in_the_address()
    {
        var h = new Handler(_ => Json("""{"data":[{"guid":"iMessage;-;+911234567890","displayName":"","chatIdentifier":"+911234567890","lastMessage":{"text":"See you"}}]}"""));
        var c = Ctx("imessage", h, cred: new() { ["url"] = "http://192.168.1.10:1234", ["password"] = "p@ss word" });
        var chats = await Load(c, "chats");
        Assert.Equal("+911234567890", Assert.Single(chats).Title);
        Assert.Contains("password=p%40ss%20word", h.Seen[0].Request.RequestUri!.Query);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "send", ("chat", "../../x"), ("text", "hi")));
        await Run(c, "send", ("chat", "iMessage;-;+911234567890"), ("text", "hi"));
        Assert.Equal("iMessage;-;+911234567890", JsonDocument.Parse(h.Seen.Last().Body).RootElement.GetProperty("chatGuid").GetString());
    }

    [Fact]
    public async Task Matrix_shows_encrypted_messages_as_locked_and_validates_room_ids()
    {
        var h = new Handler(_ => Json("""{"chunk":[{"type":"m.room.message","event_id":"$1","sender":"@amit:matrix.org","content":{"body":"hello"},"origin_server_ts":1700000000000},{"type":"m.room.encrypted","event_id":"$2","sender":"@x:matrix.org"},{"type":"m.reaction","event_id":"$3"}]}"""));
        var c = Ctx("matrix", h, cred: new() { ["homeserver"] = "https://matrix.org", ["token"] = "T" });
        var items = await Load(c, "messages", ("arg", "!abc123:matrix.org"));
        Assert.Equal(["hello", "🔒 Encrypted message"], items.Select(i => i.Title));
        Assert.Contains("%21abc123%3Amatrix.org", h.Seen[0].Request.RequestUri!.AbsoluteUri);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "messages", ("arg", "!a/../b:x")));
        await Run(c, "send", ("room", "!abc123:matrix.org"), ("text", "hi"));
        Assert.Equal(HttpMethod.Put, h.Seen.Last().Request.Method);
        Assert.Contains("/send/m.room.message/", h.Seen.Last().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Mattermost_reads_posts_in_order_and_checks_ids()
    {
        var h = new Handler(_ => Json("""{"order":["p2","p1"],"posts":{"p1":{"id":"p1","message":"first","user_id":"u1","create_at":1700000000000},"p2":{"id":"p2","message":"second","user_id":"u2","create_at":1700000001000}}}"""));
        var c = Ctx("mattermost", h, cred: new() { ["url"] = "https://chat.example.com", ["token"] = "T" });
        Assert.Equal(["second", "first"], (await Load(c, "posts", ("arg", new string('a', 26)))).Select(p => p.Title));
        await Assert.ThrowsAsync<AppException>(() => Load(c, "posts", ("arg", "../../x")));
        await Assert.ThrowsAsync<AppException>(() => Run(c, "post", ("channel", "short"), ("text", "x")));
    }

    [Fact]
    public async Task Feishu_gets_a_tenant_token_each_time_and_reads_the_json_text_body()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("tenant_access_token/internal") ? Json("""{"tenant_access_token":"TT"}""") : Json("""{"data":{"items":[{"message_id":"om_1","msg_type":"text","body":{"content":"{\"text\":\"hello team\"}"},"sender":{"id":"ou_1"},"create_time":"1700000000000"}]}}"""));
        var c = Ctx("feishu-lark", h, cred: new() { ["appId"] = "cli_x", ["appSecret"] = "s", ["domain"] = "lark" });
        var items = await Load(c, "messages", ("arg", "oc_" + new string('a', 30)));
        Assert.Equal("hello team", Assert.Single(items).Title);
        Assert.Equal("open.larksuite.com", h.Seen[0].Request.RequestUri!.Host);
        Assert.Equal("TT", h.Seen[1].Request.Headers.Authorization!.Parameter);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "messages", ("arg", "oc_../x")));
    }

    [Fact]
    public async Task Twitch_sends_its_client_id_and_resolves_the_channel_before_chatting()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/users") ? Json("""{"data":[{"id":"777","login":"somechannel"}]}""") : Json("""{"data":[{"is_sent":true}]}"""));
        var c = Ctx("twitch-chat", h, token: "T", accountId: "42", config: new() { ["Twitch:OAuth:ClientId"] = "CID" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "chat", ("channel", "bad channel!"), ("text", "hi")));
        await Run(c, "chat", ("channel", "SomeChannel"), ("text", "hi chat"));
        Assert.Equal("CID", h.Seen[0].Request.Headers.GetValues("Client-Id").Single());
        Assert.Contains("login=somechannel", h.Seen[0].Request.RequestUri!.Query);
        Assert.Contains("\"broadcaster_id\":\"777\"", h.Seen[1].Body); Assert.Contains("\"sender_id\":\"42\"", h.Seen[1].Body);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "chat", ("channel", "x"), ("text", new string('a', 501))));
    }

    [Fact]
    public async Task Twilio_sends_form_encoded_texts_with_basic_auth_and_validates_numbers()
    {
        var h = new Handler(_ => Json("""{"status":"queued","messages":[{"sid":"SM1","body":"Hi","direction":"inbound","from":"+911234567890","date_sent":"Mon, 01 Dec 2026 10:00:00 +0000","status":"received"}]}"""));
        var c = Ctx("sms-twilio", h, cred: new() { ["sid"] = "AC" + new string('a', 32), ["token"] = "tok", ["from"] = "+14155550123" });
        var r = await Run(c, "send", ("to", "+911234567890"), ("text", "Hello"));
        Assert.Equal("Text queued.", r.Message);
        Assert.Contains("To=%2B911234567890", h.Seen[0].Body); Assert.Contains("From=%2B14155550123", h.Seen[0].Body);
        Assert.Equal("Basic", h.Seen[0].Request.Headers.Authorization!.Scheme);
        await Assert.ThrowsAsync<AppException>(() => Run(c, "send", ("to", "98123"), ("text", "x")));
        var items = await Load(c, "messages");
        Assert.StartsWith("from +911234567890", Assert.Single(items).Subtitle);
    }

    [Fact]
    public async Task A2A_reads_the_card_and_only_trusts_a_service_address_on_the_same_host()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? (r.RequestUri!.AbsolutePath.EndsWith("agent-card.json") ? Json("""{"name":"Research agent","description":"Finds things","url":"https://203.0.113.9/rpc","skills":[{"id":"s1","name":"Search","description":"Looks up"}]}""") : Json("{}", HttpStatusCode.NotFound))
            : Json("""{"jsonrpc":"2.0","id":"1","result":{"parts":[{"kind":"text","text":"Found three results."}]}}"""));
        var c = Ctx("a2a", h, cred: new() { ["url"] = "https://203.0.113.9", ["token"] = "T" });
        var items = await Load(c, "agent");
        Assert.Equal(["Research agent", "Search"], items.Select(i => i.Title));
        var r = await Run(c, "send", ("text", "find x"));
        Assert.Equal("Found three results.", r.Message);
        var post = h.Seen.Last();
        Assert.Equal("https://203.0.113.9/rpc", post.Request.RequestUri!.ToString());
        Assert.Contains("\"method\":\"message/send\"", post.Body);
        // A card that points the agent somewhere else is ignored: the message goes to the host the person entered.
        var evil = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"name":"x","url":"https://evil.example.net/steal"}""") : Json("""{"result":{"status":{"state":"submitted"}}}"""));
        await Run(Ctx("a2a", evil, cred: new() { ["url"] = "https://203.0.113.9" }), "send", ("text", "hi"));
        Assert.Equal("203.0.113.9", evil.Seen.Last().Request.RequestUri!.Host);
    }

    [Fact]
    public async Task IRC_registers_joins_says_each_line_and_leaves()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream(); using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line)
            {
                received.Add(line);
                if (line.StartsWith("USER ")) { await writer.WriteLineAsync("PING :abc"); await writer.WriteLineAsync(":srv 001 daksha :Welcome to the test network"); }
                if (line.StartsWith("QUIT")) break;
            }
        });
        var welcome = await ProviderApps.IrcSendAsync("127.0.0.1", port, tls: false, "daksha", "secret", "#room", "line one\nline two", CancellationToken.None);
        await server;
        Assert.Equal("Welcome to the test network", welcome);
        Assert.Equal(["PASS secret", "NICK daksha", "USER daksha 0 * :Daksha", "PONG :abc", "JOIN #room", "PRIVMSG #room :line one", "PRIVMSG #room :line two", "QUIT :bye"], received);
    }

    [Fact]
    public async Task IRC_refuses_anything_that_could_inject_commands()
    {
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.IrcSendAsync("127.0.0.1", 1, false, "daksha\r\nQUIT", null, "#room", "x", CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.IrcSendAsync("127.0.0.1", 1, false, "daksha", null, "#room\r\nJOIN #x", "x", CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.IrcSendAsync("127.0.0.1", 1, false, "daksha", "pass word", "#room", "x", CancellationToken.None));
        var prepare = ProviderApps.Get("irc").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["host"] = "169.254.169.254", ["port"] = "6697", ["nick"] = "n" }, CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["host"] = "10.0.0.5", ["port"] = "99999", ["nick"] = "n" }, CancellationToken.None));
    }

    [Fact]
    public async Task Nextcloud_talk_sends_the_ocs_header_and_reads_ocs_data()
    {
        var h = new Handler(_ => Json("""{"ocs":{"data":[{"token":"abcd1234","displayName":"Team","unreadMessages":3,"lastMessage":{"message":"See you"}}]}}"""));
        var c = Ctx("nextcloud-talk", h, cred: new() { ["url"] = "https://cloud.example.com", ["username"] = "amit", ["password"] = "app-pw" });
        var rooms = await Load(c, "rooms");
        Assert.Equal("Team", Assert.Single(rooms).Title); Assert.Equal("3 unread", rooms[0].Badge);
        Assert.Equal("true", h.Seen[0].Request.Headers.GetValues("OCS-APIRequest").Single());
        Assert.Equal("Basic", h.Seen[0].Request.Headers.Authorization!.Scheme);
        await Assert.ThrowsAsync<AppException>(() => Load(c, "messages", ("arg", "../x")));
    }

    [Fact]
    public void Nostr_public_keys_decode_from_npub_and_private_keys_are_refused()
    {
        // Jack Dorsey's well-known public key.
        Assert.Equal("82341f882b6eabcd2ba7f1ef90aad961cf074af15b9ef44a09f9d2a8fbfbe6a2", ProviderApps.NpubToHex("npub1sg6plzptd64u62a878hep2kev88swjh3tw00gjsfl8f237lmu63q0uf63m"));
        Assert.Null(ProviderApps.NpubToHex("nsec1abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnop")); // a private key is never accepted
        Assert.Null(ProviderApps.NpubToHex("npub1!!!notbech32"));
        Assert.Null(ProviderApps.NpubToHex("hello"));
    }

    [Fact]
    public async Task Nostr_connect_only_takes_a_wss_relay_and_a_public_key()
    {
        var prepare = ProviderApps.Get("nostr").Credentials!.Prepare!;
        var good = new Dictionary<string, string> { ["relay"] = "wss://127.0.0.1", ["npub"] = "npub1sg6plzptd64u62a878hep2kev88swjh3tw00gjsfl8f237lmu63q0uf63m" };
        await prepare(good, CancellationToken.None);
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["relay"] = "ws://127.0.0.1", ["npub"] = good["npub"] }, CancellationToken.None));
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["relay"] = "wss://127.0.0.1", ["npub"] = "nsec1whatever" }, CancellationToken.None));
        Assert.False(ProviderApps.Get("nostr").HasWrite);
    }

    [Fact]
    public async Task Synology_chat_posts_a_payload_form_and_only_accepts_its_own_webhook()
    {
        var prepare = ProviderApps.Get("synology-chat").Credentials!.Prepare!;
        await Assert.ThrowsAsync<AppException>(() => prepare(new Dictionary<string, string> { ["webhook"] = "https://192.168.1.5/other" }, CancellationToken.None));
        var url = "http://192.168.1.5:5000/webapi/entry.cgi?api=SYNO.Chat.External&method=incoming&version=2&token=abc";
        var v = new Dictionary<string, string> { ["webhook"] = url }; await prepare(v, CancellationToken.None);
        var h = new Handler(_ => Json("""{"success":true}"""));
        await Run(Ctx("synology-chat", h, cred: v), "post", ("text", "Hello & goodbye"));
        Assert.StartsWith("payload=", h.Seen[0].Body);
        var payload = Uri.UnescapeDataString(h.Seen[0].Body["payload=".Length..]);
        Assert.Equal("Hello & goodbye", JsonDocument.Parse(payload).RootElement.GetProperty("text").GetString());
        Assert.Equal("application/x-www-form-urlencoded", h.Seen[0].Request.Content!.Headers.ContentType!.MediaType);
    }
}
