using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Batch 5: messaging apps. Each one reads recent conversations where the service has an API for that and sends a message with the full-access switch.
// These are not two-way assistant channels (Daksha answering your contacts); that needs inbound webhooks, which only Telegram, WhatsApp and Slack have.

public static partial class ProviderApps
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{6,14}$", RegexOptions.Compiled);

    private static IReadOnlyList<AppItem> Info(string id, string title, string subtitle) => [new AppItem(id, title, subtitle)];

    // ============================================================================================
    // Microsoft Teams (Microsoft Graph chats)
    // ============================================================================================
    private static ProviderApp Teams() => new()
    {
        Key = "microsoft-teams", Name = "Microsoft Teams", Icon = "SiMicrosoftteams", Color = "#6264A7", Account = "Your chats",
        ConnectTitle = "Connect Microsoft Teams", ConnectLabel = "Continue with Microsoft",
        ConnectBlurb = "Sign in with your work or school Microsoft account. Daksha shows your recent chats and messages.",
        WriteLabel = "Allow Daksh to send chat messages", WriteHint = "Full access lets Daksha send a message in one of your Teams chats.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#microsoft-teams",
        Ask = Rx("teams|microsoft teams|chats?"),
        Setup = ["Reuses the Microsoft app registration (`Microsoft__OAuth__ClientId` / `ClientSecret`), or set `Teams__OAuth__*`. Add the delegated Graph permissions **Chat.Read**, **User.Read** and **ChatMessage.Send** (an administrator may need to consent)."],
        Notes = "This is for reading and sending in your own chats. Making Daksha answer other people's Teams messages automatically needs a Teams bot, which is not part of this app.",
        OAuth = new()
        {
            Section = "Teams", Fallbacks = ["Microsoft"], AuthUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize", TokenUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
            ReadScopes = ["https://graph.microsoft.com/Chat.Read", "https://graph.microsoft.com/User.Read", "offline_access", "openid", "profile"], WriteScopes = ["https://graph.microsoft.com/ChatMessage.Send"], AuthExtra = new() { ["prompt"] = "select_account" },
        },
        Profile = async c => { using var d = await c.GetAsync(Graph + "me"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.First(d.RootElement, "displayName", "userPrincipalName") ?? "Microsoft account"); },
        Views =
        [
            new ViewSpec { Id = "chats", Label = "Chats", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(Graph + "me/chats?$top=25&$expand=lastMessagePreview");
                return J.Items(d.RootElement, "value", x => new AppItem(J.S(x, "id") ?? "", J.S(x, "topic") is { Length: > 0 } t ? t : J.S(x, "lastMessagePreview.from.user.displayName") is { Length: > 0 } f ? f : $"{J.S(x, "chatType")} chat", J.Clip(StripHtml(J.S(x, "lastMessagePreview.body.content")), 100), J.S(x, "webUrl"), J.When(J.S(x, "lastUpdatedDateTime")), null, "messages")); } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^[\w:@.\-]{3,200}$")) throw new AppException(400, "That chat is not valid.");
                using var d = await c.GetAsync(Graph + $"chats/{Uri.EscapeDataString(i["arg"])}/messages?$top=25");
                return J.Items(d.RootElement, "value", m => J.S(m, "messageType") == "message" ? new AppItem(J.S(m, "id") ?? "", J.Clip(StripHtml(J.S(m, "body.content")), 300), $"{J.S(m, "from.user.displayName") ?? "Someone"} · {J.When(J.S(m, "createdDateTime"))}") : null); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("chat", "Chat", "select-view", null, true, null, "chats"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["chat"], @"^[\w:@.\-]{3,200}$")) throw new AppException(400, "Choose a chat."); using var _ = await c.PostAsync(Graph + $"chats/{Uri.EscapeDataString(i["chat"])}/messages", new { body = new { content = System.Net.WebUtility.HtmlEncode(i["text"]) } }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Google Chat
    // ============================================================================================
    private const string GChat = "https://chat.googleapis.com/v1/";
    private static readonly Regex GSpace = new(@"^spaces/[\w\-]{3,60}$", RegexOptions.Compiled);

    private static ProviderApp GoogleChat() => new()
    {
        Key = "google-chat", Name = "Google Chat", Icon = "SiGooglechat", Color = "#00AC47", Account = "Your spaces and messages",
        ConnectTitle = "Connect Google Chat", ConnectLabel = "Continue with Google",
        ConnectBlurb = "Sign in with Google and Daksha shows your spaces and recent messages.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send a message in one of your spaces.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#google-chat",
        Ask = Rx("google chat|spaces?|gchat"),
        Setup = ["Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Google Chat API**, configure it (Configuration tab), and add the scopes `chat.spaces.readonly`, `chat.messages.readonly` and `chat.messages.create` to the consent screen."],
        Notes = "Google Workspace accounts only. Messages are sent as you, not as a bot.",
        OAuth = new()
        {
            Section = "GoogleChat", Fallbacks = ["Gmail", "Google"], AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
            ReadScopes = ["openid", "email", "https://www.googleapis.com/auth/chat.spaces.readonly", "https://www.googleapis.com/auth/chat.messages.readonly"], WriteScopes = ["https://www.googleapis.com/auth/chat.messages.create"],
            AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
        },
        Profile = async c => { using var d = await c.GetAsync("https://openidconnect.googleapis.com/v1/userinfo"); return new ProfileInfo(J.S(d.RootElement, "sub") ?? "", J.S(d.RootElement, "email") ?? "Google account"); },
        Views =
        [
            new ViewSpec { Id = "spaces", Label = "Spaces", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(GChat + "spaces?pageSize=50");
                return J.Items(d.RootElement, "spaces", s => new AppItem(J.S(s, "name") ?? "", J.S(s, "displayName") is { Length: > 0 } n ? n : "Direct message", J.S(s, "spaceType")?.Replace('_', ' ').ToLowerInvariant(), null, null, null, "messages")); } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!GSpace.IsMatch(i["arg"])) throw new AppException(400, "That space is not valid.");
                using var d = await c.GetAsync(GChat + $"{i["arg"]}/messages?pageSize=25&orderBy={J.E("createTime desc")}");
                return J.Items(d.RootElement, "messages", m => new AppItem(J.S(m, "name") ?? "", J.Clip(J.S(m, "text"), 300), $"{J.S(m, "sender.displayName") ?? "Someone"} · {J.When(J.S(m, "createTime"))}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("space", "Space", "select-view", null, true, null, "spaces"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!GSpace.IsMatch(i["space"])) throw new AppException(400, "Choose a space."); using var _ = await c.PostAsync(GChat + $"{i["space"]}/messages", new { text = i["text"] }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Signal (through your own signal-cli REST API bridge)
    // ============================================================================================
    private static ProviderApp Signal() => new()
    {
        Key = "signal", Name = "Signal", Icon = "SiSignal", Color = "#3A76F0", Account = "Messages through signal-cli",
        ConnectTitle = "Connect Signal", ConnectLabel = "Connect Signal",
        ConnectBlurb = "Signal has no official API, so this uses your own signal-cli REST API server (a small bridge you run). Enter its address and your Signal number.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send Signal messages from your number.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#signal",
        Ask = Rx("signal"),
        Setup = ["Nothing to set on the server. Run **signal-cli-rest-api** (the `bbernhard/signal-cli-rest-api` container) and link or register your number following its README.", "In Daksha enter the bridge address and your number in international format (`+9198…`). The Daksha server must be able to reach the bridge."],
        Notes = "Reading fetches the messages waiting on the bridge: once read they are removed from its queue.",
        Credentials = new()
        {
            Fields = [F("url", "signal-cli REST API address", "url", "http://localhost:8080"), F("number", "Your Signal number", placeholder: "+919812345678")],
            Prepare = async (v, ct) => { if (!E164.IsMatch(v["number"])) throw new AppException(400, "Enter your number like +919812345678."); await PrepareUrl("url")(v, ct); },
            Probe = async c => { using var d = await c.GetAsync(c.Cred["url"] + "/v1/about"); return new ProfileInfo(c.Cred["number"], $"Signal {c.Cred["number"]}"); },
        },
        Views =
        [
            new ViewSpec { Id = "inbox", Label = "New messages", Dashboard = true, Empty = "No new messages.", Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + $"/v1/receive/{J.E(c.Cred["number"])}?timeout=2");
                return J.Items(d.RootElement, "", m => J.S(m, "envelope.dataMessage.message") is { Length: > 0 } text ? new AppItem(J.S(m, "envelope.timestamp") ?? "", J.Clip(text, 300), $"{J.First(m, "envelope.sourceName", "envelope.sourceNumber", "envelope.source")} · {J.When((long.TryParse(J.S(m, "envelope.timestamp"), out var ms) ? ms / 1000 : 0).ToString())}") : null); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [F("to", "To", placeholder: "+919812345678"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!E164.IsMatch(i["to"])) throw new AppException(400, "Enter the number like +919812345678."); using var _ = await c.PostAsync(c.Cred["url"] + "/v2/send", new { message = i["text"], number = c.Cred["number"], recipients = new[] { i["to"] } }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // LINE (Messaging API: send only, replies arrive by webhook)
    // ============================================================================================
    private const string LineApi = "https://api.line.me/v2/bot/";

    private static ProviderApp Line() => new()
    {
        Key = "line", Name = "LINE", Icon = "SiLine", Color = "#00C300", Account = "Your LINE official account",
        ConnectTitle = "Connect LINE", ConnectLabel = "Connect LINE",
        ConnectBlurb = "Enter the channel access token of your LINE Official Account (Messaging API). Daksha can send messages to people who follow it.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha push a message to a user or broadcast to all followers.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#line",
        Ask = Rx("line|line official"),
        Setup = ["Nothing to set on the server. In the LINE Developers console create a **Messaging API channel**, issue a **long-lived channel access token**, and paste it into Daksha."],
        Notes = "LINE only delivers incoming messages by webhook, so Daksha can send but cannot list received chats.",
        Credentials = new()
        {
            Fields = [F("token", "Channel access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(LineApi + "info"); return new ProfileInfo(J.S(d.RootElement, "userId") ?? "line", J.S(d.RootElement, "displayName") ?? "LINE account"); },
        },
        Views =
        [
            new ViewSpec { Id = "account", Label = "Account", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(LineApi + "info"); using var q = await c.GetAsync(LineApi + "message/quota/consumption");
                return [new AppItem("account", J.S(d.RootElement, "displayName") ?? "LINE", $"{J.S(d.RootElement, "basicId")} · {J.S(d.RootElement, "chatMode")} mode", null, $"{J.S(q.RootElement, "totalUsage") ?? "0"} sent this month")]; } },
        ],
        Actions =
        [
            new ActionSpec { Id = "push", Label = "Message a user", Inputs = [F("to", "User id", placeholder: "U…"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["to"], @"^U[0-9a-f]{32}$")) throw new AppException(400, "A LINE user id is U followed by 32 characters."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(LineApi + "message/push", new { to = i["to"], messages = new[] { new { type = "text", text = i["text"] } } }); return new ActionResult("Sent."); } },
            new ActionSpec { Id = "broadcast", Label = "Broadcast to all followers", Hint = "Uses your monthly message quota.", Inputs = [F("text", "Message", "textarea")],
                Run = async (c, i) => { c.Token = c.Cred["token"]; using var _ = await c.PostAsync(LineApi + "message/broadcast", new { messages = new[] { new { type = "text", text = i["text"] } } }); return new ActionResult("Broadcast sent."); } },
        ],
    };

    // ============================================================================================
    // Facebook Messenger (the Page inbox) and Instagram messaging (direct messages)
    // ============================================================================================
    private static ProviderApp Messenger() => new()
    {
        Key = "facebook-messenger", Name = "Facebook Messenger", Icon = "SiMessenger", Color = "#0084FF", Account = "Your Page's Messenger inbox",
        ConnectTitle = "Connect Facebook Messenger", ConnectLabel = "Continue with Facebook",
        ConnectBlurb = "Sign in with Facebook and choose the Page to share. Daksha shows its Messenger conversations and can reply.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha reply to people who messaged your Page (within Meta's 24-hour window).",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#facebook-messenger",
        Ask = Rx("messenger|facebook messages?|page inbox"),
        ExtraSettings = [new("pageName", "Which Page", "text", "Part of the Page name, or its id. Leave empty to use the first Page.", "My business")],
        Setup = ["Uses the same Meta app as Facebook Pages (`Facebook__OAuth__ClientId` / `ClientSecret`). Add the **Messenger** product and the permission `pages_messaging`, then submit it for App Review.", "Register the extra redirect URI for this app (see below)."],
        Notes = "This is the Page's Messenger inbox. Personal Messenger chats have no public API.",
        OAuth = new()
        {
            Section = "Facebook", AuthUrl = "https://www.facebook.com/v21.0/dialog/oauth", TokenUrl = "https://graph.facebook.com/v21.0/oauth/access_token", ScopeSeparator = ",",
            ReadScopes = ["pages_show_list", "pages_messaging"], Exchange = FacebookPages().OAuth!.Exchange,
        },
        Profile = FacebookPages().Profile,
        Views =
        [
            new ViewSpec { Id = "conversations", Label = "Conversations", Dashboard = true, Load = async (c, _) => { var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{p.Id}/conversations?fields=id,snippet,updated_time,senders&platform=messenger&limit=25", Bearer(p.Token));
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "senders.data[0].name") ?? "Someone", J.Clip(J.S(m, "snippet"), 120), null, J.When(J.S(m, "updated_time")), null, "thread")); } },
            new ViewSpec { Id = "thread", Label = "Conversation", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^t_[\w.\-:]{3,100}$")) throw new AppException(400, "That conversation is not valid."); var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{J.E(i["arg"])}?fields=messages.limit(25){{id,message,from,created_time}}", Bearer(p.Token));
                return J.Items(d.RootElement, "messages.data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "message") ?? "(attachment)", $"{J.S(m, "from.name") ?? "Someone"} · {J.When(J.S(m, "created_time"))}", null, null, null, null, null)); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Reply to someone", Hint = "Use the person's id (PSID) from a conversation.", Inputs = [F("recipient", "Recipient id"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["recipient"])) throw new AppException(400, "The recipient id is a number."); var p = await FbPageAsync(c);
                    using var _ = await c.PostAsync($"{FbApi}me/messages", new { recipient = new { id = i["recipient"] }, messaging_type = "RESPONSE", message = new { text = i["text"] } }, Bearer(p.Token)); return new ActionResult("Sent."); } },
        ],
    };

    private static ProviderApp InstagramMessaging() => new()
    {
        Key = "instagram-messaging", Name = "Instagram Messaging", Icon = "SiInstagram", Color = "#E4405F", Account = "Your Instagram direct messages",
        ConnectTitle = "Connect Instagram messages", ConnectLabel = "Continue with Instagram",
        ConnectBlurb = "For Instagram business and creator accounts. Daksha shows your direct-message conversations and can reply.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha reply to messages (Instagram allows this within 24 hours of the person's last message).",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#instagram-messaging",
        Ask = Rx("instagram dms?|instagram messages?|direct messages?"),
        Setup = ["Uses the same Meta app as the Instagram app (`Instagram__OAuth__ClientId` / `ClientSecret`). Request the permission `instagram_business_manage_messages` in App Review.", "Register this app's redirect URI as well (see below)."],
        Notes = "Needs Meta App Review for the messaging permission before anyone but app testers can use it.",
        OAuth = new()
        {
            Section = "Instagram", AuthUrl = "https://www.instagram.com/oauth/authorize", TokenUrl = "https://api.instagram.com/oauth/access_token", ScopeSeparator = ",",
            ReadScopes = ["instagram_business_basic", "instagram_business_manage_messages"], AuthExtra = new() { ["enable_fb_login"] = "0", ["force_authentication"] = "1" },
            Exchange = Instagram().OAuth!.Exchange, Refresh = Instagram().OAuth!.Refresh,
        },
        Profile = Instagram().Profile,
        Views =
        [
            new ViewSpec { Id = "messages", Label = "Conversations", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{IgApi}me/conversations?platform=instagram&fields=id,updated_time,participants&limit=25");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", string.Join(", ", J.Arr(m, "participants.data").Select(p => "@" + J.S(p, "username"))), J.When(J.S(m, "updated_time")), null, null, null, "thread")); } },
            new ViewSpec { Id = "thread", Label = "Conversation", Hidden = true, TakesArg = true, Load = async (c, i) => { using var d = await c.GetAsync($"{IgApi}{J.E(i["arg"])}?fields=messages.limit(30){{message,from,created_time}}");
                return J.Items(d.RootElement, "messages.data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "message") ?? "(attachment)", $"@{J.S(m, "from.username")} · {J.When(J.S(m, "created_time"))}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "message", Label = "Send a direct message", Inputs = [F("recipient", "Recipient id"), F("text", "Message", "textarea")],
                Run = async (c, i) => { using var _ = await c.PostAsync($"{IgApi}me/messages", new { recipient = new { id = i["recipient"] }, message = new { text = i["text"] } }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // iMessage (through a BlueBubbles server running on a Mac)
    // ============================================================================================
    private static string BbBase(ProviderCtx c) => c.Cred["url"];
    private static string BbPw(ProviderCtx c) => $"password={J.E(c.Cred["password"])}";
    private static readonly Regex BbGuid = new(@"^(iMessage|SMS);[+\-];[\w@.+\-]{1,100}$", RegexOptions.Compiled);

    private static ProviderApp IMessage() => new()
    {
        Key = "imessage", Name = "iMessage", Icon = "SiImessage", Color = "#34C759", Account = "Messages through BlueBubbles on your Mac",
        ConnectTitle = "Connect iMessage", ConnectLabel = "Connect iMessage",
        ConnectBlurb = "Apple has no iMessage API, so this uses a BlueBubbles server running on your Mac. Enter its address and password.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send iMessages from your Mac.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#imessage",
        Ask = Rx("imessage|texts?|messages? app"),
        Setup = ["Nothing to set on the server. Install **BlueBubbles Server** on a Mac that stays on and signed in to Messages (bluebubbles.app), then note its address and password.", "The Daksha server must be able to reach the Mac (use a private tunnel for a cloud server)."],
        Notes = "The BlueBubbles API takes the password as part of the address, so keep the connection on a trusted network.",
        Credentials = new()
        {
            Fields = [F("url", "BlueBubbles address", "url", "http://192.168.1.10:1234"), F("password", "Server password", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await c.GetAsync($"{BbBase(c)}/api/v1/server/info?{BbPw(c)}"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"BlueBubbles {J.S(d.RootElement, "data.server_version")}".Trim()); },
        },
        Views =
        [
            new ViewSpec { Id = "chats", Label = "Chats", Dashboard = true, Load = async (c, _) => { using var d = await c.PostAsync($"{BbBase(c)}/api/v1/chat/query?{BbPw(c)}", new { limit = 25, offset = 0, with = new[] { "lastMessage" }, sort = "lastmessage" });
                return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "guid") ?? "", J.First(x, "displayName", "chatIdentifier") ?? "Chat", J.Clip(J.S(x, "lastMessage.text"), 100), null, null, null, "messages")); } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!BbGuid.IsMatch(i["arg"])) throw new AppException(400, "That chat is not valid.");
                using var d = await c.GetAsync($"{BbBase(c)}/api/v1/chat/{Uri.EscapeDataString(i["arg"])}/message?{BbPw(c)}&limit=25&sort=DESC");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "guid") ?? "", J.Clip(J.S(m, "text"), 300), $"{(J.S(m, "isFromMe") == "true" ? "Me" : J.S(m, "handle.address") ?? "Someone")} · {J.When((long.TryParse(J.S(m, "dateCreated"), out var ms) ? ms / 1000 : 0).ToString())}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("chat", "Chat", "select-view", null, true, null, "chats"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!BbGuid.IsMatch(i["chat"])) throw new AppException(400, "Choose a chat."); using var _ = await c.PostAsync($"{BbBase(c)}/api/v1/message/text?{BbPw(c)}", new { chatGuid = i["chat"], tempGuid = Guid.NewGuid().ToString(), message = i["text"], method = "apple-script" }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Matrix (any homeserver, with an access token)
    // ============================================================================================
    private static string MxBase(ProviderCtx c) => c.Cred["homeserver"] + "/_matrix/client/v3/";
    private static readonly Regex MxRoom = new(@"^![\w\-.=]{1,100}:[\w.\-]{1,100}(:\d{2,5})?$", RegexOptions.Compiled);

    private static ProviderApp Matrix() => new()
    {
        Key = "matrix", Name = "Matrix", Icon = "SiMatrix", Color = "#000000", Account = "Your rooms and messages",
        ConnectTitle = "Connect Matrix", ConnectLabel = "Connect Matrix",
        ConnectBlurb = "Enter your homeserver and an access token (Element → Settings → Help & About → Access Token). Daksha shows your rooms and recent messages.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send messages in your rooms.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#matrix",
        Ask = Rx("matrix|element|rooms?"),
        NoBearer = false,
        Setup = ["Nothing to set on the server. Each person copies their **access token** from their Matrix client (Element: Settings → Help & About → Advanced → Access Token), or creates a separate bot account and logs it in.", "In Daksha they enter the homeserver address (`https://matrix.org`) and the token."],
        Notes = "Rooms with end-to-end encryption show as encrypted: Daksha cannot read them.",
        Credentials = new()
        {
            Fields = [F("homeserver", "Homeserver", "url", "https://matrix.org"), F("token", "Access token", "secret")],
            Prepare = PrepareUrl("homeserver"),
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(MxBase(c) + "account/whoami"); return new ProfileInfo(J.S(d.RootElement, "user_id") ?? "matrix", J.S(d.RootElement, "user_id") ?? "Matrix"); },
        },
        Views =
        [
            new ViewSpec { Id = "rooms", Label = "Rooms", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(MxBase(c) + "joined_rooms"); var items = new List<AppItem>();
                foreach (var id in J.Arr(d.RootElement, "joined_rooms").Select(r => r.GetString() ?? "").Where(r => MxRoom.IsMatch(r)).Take(20))
                {
                    string name = id; try { using var n = await c.GetAsync(MxBase(c) + $"rooms/{Uri.EscapeDataString(id)}/state/m.room.name"); name = J.S(n.RootElement, "name") ?? id; } catch (AppException) { }
                    items.Add(new AppItem(id, name, id, null, null, null, "messages"));
                } return items; } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!MxRoom.IsMatch(i["arg"])) throw new AppException(400, "That room is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(MxBase(c) + $"rooms/{Uri.EscapeDataString(i["arg"])}/messages?dir=b&limit=25");
                return J.Items(d.RootElement, "chunk", e => J.S(e, "type") switch { "m.room.message" => new AppItem(J.S(e, "event_id") ?? "", J.Clip(J.S(e, "content.body"), 300), $"{J.S(e, "sender")} · {J.When((long.TryParse(J.S(e, "origin_server_ts"), out var ms) ? ms / 1000 : 0).ToString())}"), "m.room.encrypted" => new AppItem(J.S(e, "event_id") ?? "", "🔒 Encrypted message", J.S(e, "sender")), _ => null }); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("room", "Room", "select-view", null, true, null, "rooms"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!MxRoom.IsMatch(i["room"])) throw new AppException(400, "Choose a room."); c.Token = c.Cred["token"]; using var _ = await c.JsonAsync(HttpMethod.Put, MxBase(c) + $"rooms/{Uri.EscapeDataString(i["room"])}/send/m.room.message/{Guid.NewGuid():N}", new { msgtype = "m.text", body = i["text"] }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Mattermost
    // ============================================================================================
    private static readonly Regex MmId = new("^[a-z0-9]{26}$", RegexOptions.Compiled);

    private static ProviderApp Mattermost() => new()
    {
        Key = "mattermost", Name = "Mattermost", Icon = "SiMattermost", Color = "#0058CC", Account = "Teams, channels and posts",
        ConnectTitle = "Connect Mattermost", ConnectLabel = "Connect Mattermost",
        ConnectBlurb = "Enter your Mattermost address and a personal access token (Profile → Security → Personal Access Tokens).",
        WriteLabel = "Allow Daksh to post messages", WriteHint = "Full access lets Daksha post in channels you can write to.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#mattermost",
        Ask = Rx("mattermost"),
        Setup = ["Nothing to set on the server. Personal access tokens must be enabled in the System Console; then each person creates one under Profile → Security → Personal Access Tokens."],
        Credentials = new()
        {
            Fields = [F("url", "Mattermost address", "url", "https://chat.example.com"), F("token", "Personal access token", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/api/v4/users/me"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", "@" + (J.S(d.RootElement, "username") ?? "me") + " on " + new Uri(c.Cred["url"]).Host); },
        },
        Views =
        [
            new ViewSpec { Id = "teams", Label = "Teams", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/api/v4/users/me/teams");
                return J.Items(d.RootElement, "", t => new AppItem(J.S(t, "id") ?? "", J.S(t, "display_name") ?? "Team", J.S(t, "description"), null, null, null, "channels")); } },
            new ViewSpec { Id = "channels", Label = "Channels", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!MmId.IsMatch(i["arg"])) throw new AppException(400, "That team is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(c.Cred["url"] + $"/api/v4/users/me/teams/{i["arg"]}/channels");
                return J.Items(d.RootElement, "", ch => new AppItem(J.S(ch, "id") ?? "", J.First(ch, "display_name", "name") is { Length: > 0 } n ? n : "Direct message", J.Clip(J.S(ch, "header") ?? J.S(ch, "purpose"), 90), null, J.S(ch, "type") switch { "D" => "direct", "G" => "group", "P" => "private", _ => null }, null, "posts"), 100); } },
            new ViewSpec { Id = "posts", Label = "Posts", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!MmId.IsMatch(i["arg"])) throw new AppException(400, "That channel is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(c.Cred["url"] + $"/api/v4/channels/{i["arg"]}/posts?per_page=25");
                return J.Arr(d.RootElement, "order").Select(id => J.At(d.RootElement, "posts." + id.GetString())).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new AppItem(J.S(p, "id") ?? "", J.Clip(J.S(p, "message"), 300), $"{J.S(p, "user_id")} · {J.When((long.TryParse(J.S(p, "create_at"), out var ms) ? ms / 1000 : 0).ToString())}")).ToList(); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Post a message", Inputs = [F("channel", "Channel id", placeholder: "From the Channels list"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!MmId.IsMatch(i["channel"])) throw new AppException(400, "Enter the 26-character channel id."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(c.Cred["url"] + "/api/v4/posts", new { channel_id = i["channel"], message = i["text"] }); return new ActionResult("Posted."); } },
        ],
    };

    // ============================================================================================
    // Feishu / Lark (a custom app: app id and secret)
    // ============================================================================================
    private static string LarkBase(ProviderCtx c) => c.Cred.GetValueOrDefault("domain") == "lark" ? "https://open.larksuite.com" : "https://open.feishu.cn";

    private static async Task LarkLoginAsync(ProviderCtx c)
    {
        using var d = await c.PostAsync(LarkBase(c) + "/open-apis/auth/v3/tenant_access_token/internal", new { app_id = c.Cred["appId"], app_secret = c.Cred["appSecret"] });
        c.Token = J.S(d.RootElement, "tenant_access_token") ?? throw new AppException(409, $"Feishu/Lark did not accept the app credentials ({J.S(d.RootElement, "msg")}).", "reconnect_required");
    }

    private static ProviderApp Feishu() => new()
    {
        Key = "feishu-lark", Name = "Feishu / Lark", Icon = "mono:FS", Color = "#00D6B9", Account = "Group chats your app is in",
        ConnectTitle = "Connect Feishu / Lark", ConnectLabel = "Connect Feishu / Lark",
        ConnectBlurb = "Create a custom app in the Feishu or Lark developer console and enter its App ID and App Secret. Daksha shows the group chats the app is in and can post there.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send messages as the app.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#feishu-lark",
        Ask = Rx("feishu|lark"),
        Setup = ["Nothing to set on the server. In open.feishu.cn (or open.larksuite.com) create a **custom app**, enable the **Bot** capability, add the permissions `im:chat:readonly`, `im:message:readonly` and `im:message:send_as_bot`, publish it, and add the bot to the chats you want.", "In Daksha enter the App ID, the App Secret and choose Feishu or Lark."],
        Credentials = new()
        {
            Fields = [F("appId", "App ID", placeholder: "cli_…"), F("appSecret", "App Secret", "secret"), new("domain", "Service", "select", null, true, ["feishu", "lark"], null, "feishu")],
            Probe = async c => { await LarkLoginAsync(c); return new ProfileInfo(c.Cred["appId"], $"{(c.Cred["domain"] == "lark" ? "Lark" : "Feishu")} app {c.Cred["appId"]}"); },
        },
        Views =
        [
            new ViewSpec { Id = "chats", Label = "Chats", Dashboard = true, Load = async (c, _) => { await LarkLoginAsync(c); using var d = await c.GetAsync(LarkBase(c) + "/open-apis/im/v1/chats?page_size=20");
                return J.Items(d.RootElement, "data.items", x => new AppItem(J.S(x, "chat_id") ?? "", J.S(x, "name") is { Length: > 0 } n ? n : "Chat", J.Clip(J.S(x, "description"), 90), null, null, null, "messages")); } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^oc_[0-9a-f]{20,40}$")) throw new AppException(400, "That chat is not valid."); await LarkLoginAsync(c);
                using var d = await c.GetAsync(LarkBase(c) + $"/open-apis/im/v1/messages?container_id_type=chat&container_id={i["arg"]}&page_size=20&sort_type=ByCreateTimeDesc");
                return J.Items(d.RootElement, "data.items", m => { string text = ""; try { using var body = JsonDocument.Parse(J.S(m, "body.content") ?? "{}"); text = J.S(body.RootElement, "text") ?? $"({J.S(m, "msg_type")})"; } catch (JsonException) { text = $"({J.S(m, "msg_type")})"; }
                    return new AppItem(J.S(m, "message_id") ?? "", J.Clip(text, 300), $"{J.S(m, "sender.id")} · {J.When((long.TryParse(J.S(m, "create_time"), out var ms) ? ms / 1000 : 0).ToString())}"); }); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("chat", "Chat", "select-view", null, true, null, "chats"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["chat"], @"^oc_[0-9a-f]{20,40}$")) throw new AppException(400, "Choose a chat."); await LarkLoginAsync(c);
                    using var _ = await c.PostAsync(LarkBase(c) + "/open-apis/im/v1/messages?receive_id_type=chat_id", new { receive_id = i["chat"], msg_type = "text", content = JsonSerializer.Serialize(new { text = i["text"] }) }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Twitch (followed channels that are live; send a chat message)
    // ============================================================================================
    private const string Helix = "https://api.twitch.tv/helix/";

    private static ProviderApp Twitch() => new()
    {
        Key = "twitch-chat", Name = "Twitch Chat", Icon = "SiTwitch", Color = "#9146FF", Account = "Live channels and chat",
        ConnectTitle = "Connect Twitch", ConnectLabel = "Continue with Twitch",
        ConnectBlurb = "Sign in with Twitch and Daksha shows which channels you follow are live, and can send a chat message as you.",
        WriteLabel = "Allow Daksh to send chat messages", WriteHint = "Full access lets Daksha send a message in a channel's chat as you.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#twitch-chat",
        Ask = Rx("twitch|streams?|streamers?"),
        Decorate = (r, c) => r.Headers.TryAddWithoutValidation("Client-Id", c.Server("Twitch:OAuth:ClientId")),
        Setup = ["dev.twitch.tv → Console → **Register Your Application** (Category: Website Integration, Client Type: Confidential). Copy the client id and generate a secret."],
        Notes = "Reading a chat as it happens needs a live connection, so Daksha lists live followed channels and sends messages but does not stream chat.",
        OAuth = new() { Section = "Twitch", AuthUrl = "https://id.twitch.tv/oauth2/authorize", TokenUrl = "https://id.twitch.tv/oauth2/token", ReadScopes = ["user:read:follows"], WriteScopes = ["user:write:chat"] },
        Profile = async c => { using var d = await c.GetAsync(Helix + "users"); var u = J.Arr(d.RootElement, "data").FirstOrDefault(); return new ProfileInfo(J.S(u, "id") ?? throw new AppException(502, "Twitch did not return the account."), J.First(u, "display_name", "login") ?? "Twitch"); },
        Views =
        [
            new ViewSpec { Id = "live", Label = "Live now", Dashboard = true, Empty = "None of the channels you follow are live.", Load = async (c, _) => { using var d = await c.GetAsync(Helix + $"streams/followed?user_id={J.E(c.Account.AccountId)}&first=25");
                return J.Items(d.RootElement, "data", s => new AppItem(J.S(s, "user_login") ?? "", J.S(s, "user_name") ?? "Channel", $"{J.S(s, "game_name")} · {J.Clip(J.S(s, "title"), 80)}", $"https://www.twitch.tv/{J.S(s, "user_login")}", $"{J.S(s, "viewer_count")} watching")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "chat", Label = "Send a chat message", Inputs = [F("channel", "Channel", placeholder: "channel name"), F("text", "Message")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["channel"], @"^[A-Za-z0-9_]{1,25}$")) throw new AppException(400, "A channel name is letters, numbers and underscores."); if (i["text"].Length > 500) throw new AppException(400, "Twitch messages can be at most 500 characters.");
                    using var u = await c.GetAsync(Helix + $"users?login={i["channel"].ToLowerInvariant()}"); var id = J.S(J.Arr(u.RootElement, "data").FirstOrDefault(), "id") ?? throw new AppException(404, "That channel was not found.");
                    using var d = await c.PostAsync(Helix + "chat/messages", new { broadcaster_id = id, sender_id = c.Account.AccountId, message = i["text"] });
                    if (J.S(d.RootElement, "data[0].is_sent") == "false") throw new AppException(400, $"Twitch did not send it: {J.S(d.RootElement, "data[0].drop_reason.message")}"); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // SMS through Twilio
    // ============================================================================================
    private static string TwilioBase(ProviderCtx c) => $"https://api.twilio.com/2010-04-01/Accounts/{J.E(c.Cred["sid"])}/";

    private static ProviderApp TwilioSms() => new()
    {
        Key = "sms-twilio", Name = "SMS (Twilio)", Icon = "SiTwilio", Color = "#F22F46", Account = "Text messages sent and received",
        ConnectTitle = "Connect Twilio SMS", ConnectLabel = "Connect Twilio",
        ConnectBlurb = "Enter your Twilio Account SID, Auth Token and the Twilio number to send from. Daksha shows recent messages and can send a text.",
        WriteLabel = "Allow Daksh to send text messages", WriteHint = "Full access lets Daksha send SMS from your Twilio number (each text is billed by Twilio).",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#sms-twilio",
        Ask = Rx("sms|texts?|text messages?|twilio"),
        NoBearer = true, Decorate = BasicFromCred("sid", "token"),
        Setup = ["Nothing to set on the server. Each person copies the **Account SID** and **Auth Token** from console.twilio.com and the phone number (in E.164 form) that messages should be sent from."],
        Notes = "Trial accounts can only text verified numbers. Every text sent is billed to the Twilio account.",
        Credentials = new()
        {
            Fields = [F("sid", "Account SID", placeholder: "AC…"), F("token", "Auth token", "secret"), F("from", "Send from", placeholder: "+14155550123")],
            Prepare = (v, _) => Regex.IsMatch(v["sid"], "^AC[0-9a-f]{32}$") && E164.IsMatch(v["from"]) ? Task.CompletedTask : throw new AppException(400, "Check the Account SID (AC…) and the number (+14155550123)."),
            Probe = async c => { using var d = await c.GetAsync(TwilioBase(c) + "Messages.json?PageSize=1"); return new ProfileInfo(c.Cred["sid"], $"Twilio {c.Cred["from"]}"); },
        },
        Views =
        [
            new ViewSpec { Id = "messages", Label = "Recent messages", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(TwilioBase(c) + "Messages.json?PageSize=25");
                return J.Items(d.RootElement, "messages", m => new AppItem(J.S(m, "sid") ?? "", J.Clip(J.S(m, "body"), 200), $"{(J.S(m, "direction")?.StartsWith("inbound") == true ? "from " + J.S(m, "from") : "to " + J.S(m, "to"))} · {J.When(J.S(m, "date_sent"))}", null, J.S(m, "status"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a text", Inputs = [F("to", "To", placeholder: "+919812345678"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!E164.IsMatch(i["to"])) throw new AppException(400, "Enter the number like +919812345678."); if (i["text"].Length > 1600) throw new AppException(400, "A text can be at most 1,600 characters.");
                    using var d = await c.PostFormAsync(TwilioBase(c) + "Messages.json", new Dictionary<string, string> { ["To"] = i["to"], ["From"] = c.Cred["from"], ["Body"] = i["text"] }); return new ActionResult($"Text {J.S(d.RootElement, "status") ?? "sent"}."); } },
        ],
    };

    // ============================================================================================
    // A2A (Agent2Agent protocol: talk to another agent)
    // ============================================================================================
    private static async Task<JsonDocument> A2aCardAsync(ProviderCtx c)
    {
        foreach (var path in new[] { "/.well-known/agent-card.json", "/.well-known/agent.json" })
        {
            try { return await c.GetAsync(c.Cred["url"] + path); } catch (AppException ex) when (ex.StatusCode == 404) { }
        }
        throw new AppException(404, "No agent card was found at that address (/.well-known/agent-card.json).");
    }

    private static ProviderApp A2a() => new()
    {
        Key = "a2a", Name = "A2A", Icon = "mono:A2A", Color = "#64748B", Account = "Another agent over the Agent2Agent protocol",
        ConnectTitle = "Connect an A2A agent", ConnectLabel = "Connect agent",
        ConnectBlurb = "Enter the address of an agent that speaks the Agent2Agent protocol. Daksha reads its agent card and can send it messages.",
        WriteLabel = "Allow Daksh to message the agent", WriteHint = "Agents can act on what they are told, so sending a message needs full access.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#a2a",
        Ask = Rx("a2a|agent2agent|other agent|remote agent"),
        Setup = ["Nothing to set on the server. Each person enters the base address of an A2A agent (it serves `/.well-known/agent-card.json`) and a bearer token if the agent requires one."],
        Notes = "Uses the JSON-RPC `message/send` method over HTTP.",
        Credentials = new()
        {
            Fields = [F("url", "Agent address", "url", "https://agent.example.com"), F("token", "Bearer token", "secret", required: false)],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await A2aCardAsync(c); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, J.S(d.RootElement, "name") ?? "A2A agent"); },
        },
        Views =
        [
            new ViewSpec { Id = "agent", Label = "Agent", Dashboard = true, Load = async (c, _) => { using var d = await A2aCardAsync(c);
                return [new AppItem("agent", J.S(d.RootElement, "name") ?? "Agent", J.Clip(J.S(d.RootElement, "description"), 160), J.S(d.RootElement, "url"), J.S(d.RootElement, "version")), .. J.Arr(d.RootElement, "skills").Select(s => new AppItem(J.S(s, "id") ?? "", J.S(s, "name") ?? "Skill", J.Clip(J.S(s, "description"), 140)))]; } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send the agent a message", Inputs = [F("text", "Message", "textarea")],
                Run = async (c, i) =>
                {
                    using var card = await A2aCardAsync(c);
                    var endpoint = J.S(card.RootElement, "url") is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == new Uri(c.Cred["url"]).Host ? u : c.Cred["url"];
                    if (c.Cred.GetValueOrDefault("token") is { Length: > 0 } t) c.Token = t;
                    using var d = await c.PostAsync(endpoint, new { jsonrpc = "2.0", id = Guid.NewGuid().ToString(), method = "message/send", @params = new { message = new { role = "user", messageId = Guid.NewGuid().ToString(), parts = new[] { new { kind = "text", text = i["text"] } } } } });
                    if (J.At(d.RootElement, "error").ValueKind == JsonValueKind.Object) throw new AppException(400, $"The agent said: {J.S(d.RootElement, "error.message")}");
                    return new ActionResult(J.Clip(J.First(d.RootElement, "result.parts[0].text", "result.status.message.parts[0].text", "result.artifacts[0].parts[0].text") ?? $"The agent accepted the task ({J.S(d.RootElement, "result.status.state") ?? "submitted"}).", 800));
                } },
        ],
    };

    // ============================================================================================
    // IRC (a real client: connects, says one thing and leaves)
    // ============================================================================================
    private static readonly Regex IrcNick = new(@"^[A-Za-z\[\]\\`_^{|}][A-Za-z0-9\[\]\\`_^{|}\-]{0,29}$", RegexOptions.Compiled);
    private static readonly Regex IrcTarget = new(@"^[#&][^\s,\x07]{1,50}$|^[A-Za-z\[\]\\`_^{|}][A-Za-z0-9\[\]\\`_^{|}\-]{0,29}$", RegexOptions.Compiled);

    /// <summary>Connects, registers, optionally joins a channel, sends the lines, and disconnects. Returns the server's welcome line.</summary>
    public static async Task<string> IrcSendAsync(string host, int port, bool tls, string nick, string? password, string target, string text, CancellationToken ct)
    {
        if (!IrcNick.IsMatch(nick) || !IrcTarget.IsMatch(target)) throw new AppException(400, "Check the nickname and the channel (a channel starts with #).");
        if (password is not null && (password.Contains('\r') || password.Contains('\n') || password.Contains(' '))) throw new AppException(400, "The password cannot contain spaces or line breaks.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(host, port, cts.Token); } catch (Exception ex) when (ex is SocketException or OperationCanceledException) { throw new AppException(502, "Daksha could not reach that IRC server."); }
        Stream stream = tcp.GetStream();
        if (tls) { var ssl = new System.Net.Security.SslStream(stream); await ssl.AuthenticateAsClientAsync(host); stream = ssl; }
        using var reader = new StreamReader(stream, Encoding.UTF8); using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        async Task Send(string line) => await writer.WriteLineAsync(line.Replace("\r", " ").Replace("\n", " "));
        if (password is { Length: > 0 }) await Send($"PASS {password}");
        await Send($"NICK {nick}"); await Send($"USER {nick} 0 * :Daksha");
        string welcome = "";
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("PING ", StringComparison.Ordinal)) await Send("PONG " + line[5..]);
                var parts = line.Split(' ');
                if (parts.Length > 1 && parts[1] == "001") { welcome = line[(line.IndexOf(':', 1) + 1)..]; break; }
                if (parts.Length > 1 && parts[1] is "433" or "464" or "465") throw new AppException(409, parts[1] == "433" ? "That nickname is already in use. Choose another." : "The IRC server refused the password.");
            }
        }
        catch (OperationCanceledException) { throw new AppException(504, "The IRC server did not answer in time."); }
        if (welcome.Length == 0) throw new AppException(502, "The IRC server closed the connection.");
        if (target[0] is '#' or '&') await Send($"JOIN {target}");
        foreach (var chunk in Regex.Split(text.Replace("\r", ""), "\n").Where(l => l.Trim().Length > 0).Take(5)) await Send($"PRIVMSG {target} :{(chunk.Length > 400 ? chunk[..400] : chunk)}");
        await Send("QUIT :bye");
        return welcome;
    }

    private static ProviderApp Irc() => new()
    {
        Key = "irc", Name = "IRC", Icon = "mono:IRC", Color = "#64748B", Account = "Send messages to IRC channels and people",
        ConnectTitle = "Connect IRC", ConnectLabel = "Connect IRC",
        ConnectBlurb = "Enter an IRC server and the nickname Daksha should use. Daksha connects only when it sends a message, says it and leaves.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha send messages to channels and people as your nickname.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#irc",
        Ask = Rx("irc"),
        Setup = ["Nothing to set on the server. Each person enters an IRC server (for example `irc.libera.chat`), a port (`6697` is TLS), a nickname and a server password if the network uses one."],
        Notes = "IRC keeps no history, so Daksha can only send. Networks may require a registered nickname to message channels.",
        Credentials = new()
        {
            Fields = [F("host", "Server", placeholder: "irc.libera.chat"), F("port", "Port", "number", def: "6697"), F("nick", "Nickname", placeholder: "daksha-amit"), F("password", "Server password", "secret", required: false), F("channel", "Default channel", placeholder: "#daksha", required: false)],
            Prepare = async (v, ct) => { await HostGuard.CheckHostAsync(v["host"].Trim(), ct); if (!int.TryParse(v["port"], out var p) || p is < 1 or > 65535) throw new AppException(400, "Enter a port between 1 and 65535."); if (!IrcNick.IsMatch(v["nick"])) throw new AppException(400, "That is not a valid IRC nickname."); },
            Probe = c => Task.FromResult(new ProfileInfo($"{c.Cred["host"]}/{c.Cred["nick"]}", $"{c.Cred["nick"]} on {c.Cred["host"]}")),
        },
        Views = [new ViewSpec { Id = "server", Label = "Server", Dashboard = true, Load = (c, _) => Task.FromResult(Info("server", c.Cred["host"], $"Nickname {c.Cred["nick"]}{(c.Cred.GetValueOrDefault("channel") is { Length: > 0 } ch ? " · default channel " + ch : "")}")) }],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [F("target", "Channel or nickname", placeholder: "#daksha"), F("text", "Message", "textarea")],
                Run = async (c, i) => { var welcome = await IrcSendAsync(c.Cred["host"], int.Parse(c.Cred["port"]), int.Parse(c.Cred["port"]) is 6697 or 7000 or 6670, c.Cred["nick"], c.Cred.GetValueOrDefault("password"), i["target"], i["text"], c.Ct); return new ActionResult($"Sent. {J.Clip(welcome, 80)}"); } },
        ],
    };

    // ============================================================================================
    // Nextcloud Talk
    // ============================================================================================
    private static async Task<JsonDocument> NcAsync(ProviderCtx c, HttpMethod m, string path, object? body = null) =>
        await c.JsonAsync(m, c.Cred["url"] + "/ocs/v2.php/apps/spreed/api/" + path, body, headers: new Dictionary<string, string> { ["OCS-APIRequest"] = "true", ["Accept"] = "application/json" });

    private static ProviderApp NextcloudTalk() => new()
    {
        Key = "nextcloud-talk", Name = "Nextcloud Talk", Icon = "SiNextcloud", Color = "#0082C9", Account = "Conversations and messages",
        ConnectTitle = "Connect Nextcloud Talk", ConnectLabel = "Connect Nextcloud",
        ConnectBlurb = "Enter your Nextcloud address, username and an app password (Settings → Security → Devices & sessions). Daksha shows your conversations and can post.",
        WriteLabel = "Allow Daksh to send messages", WriteHint = "Full access lets Daksha post in your conversations.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#nextcloud-talk",
        Ask = Rx("nextcloud|nextcloud talk"),
        NoBearer = true, Decorate = BasicFromCred("username", "password"),
        Setup = ["Nothing to set on the server. Each person creates an **app password** in Nextcloud (Settings → Security → Devices & sessions) and enters it with their address and username."],
        Credentials = new()
        {
            Fields = [F("url", "Nextcloud address", "url", "https://cloud.example.com"), F("username", "Username"), F("password", "App password", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await NcAsync(c, HttpMethod.Get, "v4/room"); return new ProfileInfo($"{c.Cred["username"]}@{new Uri(c.Cred["url"]).Authority}", $"{c.Cred["username"]} on {new Uri(c.Cred["url"]).Host}"); },
        },
        Views =
        [
            new ViewSpec { Id = "rooms", Label = "Conversations", Dashboard = true, Load = async (c, _) => { using var d = await NcAsync(c, HttpMethod.Get, "v4/room");
                return J.Items(d.RootElement, "ocs.data", r => new AppItem(J.S(r, "token") ?? "", J.S(r, "displayName") ?? "Conversation", J.Clip(J.S(r, "lastMessage.message"), 90), null, J.S(r, "unreadMessages") is { } u && u != "0" ? $"{u} unread" : null, null, "messages")); } },
            new ViewSpec { Id = "messages", Label = "Messages", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], "^[a-z0-9]{4,30}$")) throw new AppException(400, "That conversation is not valid.");
                using var d = await NcAsync(c, HttpMethod.Get, $"v1/chat/{i["arg"]}?lookIntoFuture=0&limit=25");
                return J.Items(d.RootElement, "ocs.data", m => new AppItem(J.S(m, "id") ?? "", J.Clip(J.S(m, "message"), 300), $"{J.S(m, "actorDisplayName")} · {J.When(J.S(m, "timestamp"))}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send a message", Inputs = [new("room", "Conversation", "select-view", null, true, null, "rooms"), F("text", "Message", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["room"], "^[a-z0-9]{4,30}$")) throw new AppException(400, "Choose a conversation."); using var _ = await NcAsync(c, HttpMethod.Post, $"v1/chat/{i["room"]}", new { message = i["text"] }); return new ActionResult("Sent."); } },
        ],
    };

    // ============================================================================================
    // Nostr (read-only: notes from a public key, straight from a relay)
    // ============================================================================================
    private const string Bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>Turns an npub… address into its 64-character hex public key. Returns null when it is not a valid npub.</summary>
    public static string? NpubToHex(string npub)
    {
        var s = npub.Trim().ToLowerInvariant();
        if (!s.StartsWith("npub1", StringComparison.Ordinal) || s.Length < 20) return null;
        var data = new List<int>();
        foreach (var ch in s[5..]) { var v = Bech32Chars.IndexOf(ch); if (v < 0) return null; data.Add(v); }
        if (data.Count < 7) return null;
        data.RemoveRange(data.Count - 6, 6); // the checksum
        int acc = 0, bits = 0; var bytes = new List<byte>();
        foreach (var v in data) { acc = (acc << 5) | v; bits += 5; while (bits >= 8) { bits -= 8; bytes.Add((byte)((acc >> bits) & 0xff)); } }
        return bytes.Count == 32 ? Convert.ToHexStringLower(bytes.ToArray()) : null;
    }

    private static async Task<List<JsonDocument>> NostrNotesAsync(string relay, string hexKey, CancellationToken ct)
    {
        var notes = new List<JsonDocument>();
        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await ws.ConnectAsync(new Uri(relay), cts.Token);
            var req = JsonSerializer.Serialize(new object[] { "REQ", "daksha", new { authors = new[] { hexKey }, kinds = new[] { 1 }, limit = 25 } });
            await ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, cts.Token);
            var buffer = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buffer, cts.Token); if (r.MessageType == WebSocketMessageType.Close) return notes; ms.Write(buffer, 0, r.Count); if (ms.Length > 512 * 1024) return notes; } while (!r.EndOfMessage);
                var doc = JsonDocument.Parse(ms.ToArray()); var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 && root[0].GetString() == "EVENT") notes.Add(JsonDocument.Parse(root[2].GetRawText()));
                else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].GetString() is "EOSE" or "CLOSED") break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or UriFormatException or JsonException) { if (notes.Count == 0) throw new AppException(502, "Daksha could not read from that Nostr relay."); }
        finally { try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { } }
        return notes;
    }

    private static ProviderApp Nostr() => new()
    {
        Key = "nostr", Name = "Nostr", Icon = "mono:NO", Color = "#8B5CF6", Account = "Notes from a public key",
        ConnectTitle = "Connect Nostr", ConnectLabel = "Connect Nostr",
        ConnectBlurb = "Enter a relay and a public key (npub…). Daksha reads the latest notes from it. It never asks for your private key.",
        WriteLabel = "Full access", WriteHint = "Posting needs your private key, which Daksha never handles. Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#nostr",
        Ask = Rx("nostr|notes? from"),
        Setup = ["Nothing to set on the server. Each person enters a **relay** address (`wss://relay.damus.io`) and a **public key** (`npub1…`)."],
        Notes = "Read-only by design: publishing and direct messages require signing with a private key, which Daksha does not store.",
        Credentials = new()
        {
            Fields = [F("relay", "Relay", "url", "wss://relay.damus.io", def: "wss://relay.damus.io"), F("npub", "Public key", placeholder: "npub1…")],
            Prepare = async (v, ct) => { if (NpubToHex(v["npub"]) is null) throw new AppException(400, "Enter a public key that starts with npub1 (never a private key)."); if (!Uri.TryCreate(v["relay"], UriKind.Absolute, out var u) || u.Scheme != "wss") throw new AppException(400, "A relay address starts with wss://."); await HostGuard.CheckHostAsync(u.Host, ct); },
            Probe = c => Task.FromResult(new ProfileInfo(NpubToHex(c.Cred["npub"])!, $"{c.Cred["npub"][..12]}… on {new Uri(c.Cred["relay"]).Host}")),
        },
        Views =
        [
            new ViewSpec { Id = "notes", Label = "Notes", Dashboard = true, Empty = "No notes were found on that relay.", Load = async (c, _) => { var notes = await NostrNotesAsync(c.Cred["relay"], NpubToHex(c.Cred["npub"])!, c.Ct);
                try { return notes.OrderByDescending(n => long.TryParse(J.S(n.RootElement, "created_at"), out var t) ? t : 0).Select(n => new AppItem(J.S(n.RootElement, "id") ?? "", J.Clip(J.S(n.RootElement, "content"), 300), J.When(J.S(n.RootElement, "created_at")))).ToList(); } finally { foreach (var n in notes) n.Dispose(); } } },
        ],
    };

    // ============================================================================================
    // Synology Chat (an incoming webhook on your NAS)
    // ============================================================================================
    private static ProviderApp SynologyChat() => new()
    {
        Key = "synology-chat", Name = "Synology Chat", Icon = "SiSynology", Color = "#B5B5B6", Account = "A channel on your NAS",
        ConnectTitle = "Connect Synology Chat", ConnectLabel = "Connect Synology Chat",
        ConnectBlurb = "Create an incoming webhook in Synology Chat (Integration → Incoming Webhooks) and paste its address. Daksha can post to that channel.",
        WriteLabel = "Allow Daksh to post messages", WriteHint = "Full access lets Daksha post to the channel.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#synology-chat",
        Ask = Rx("synology|synology chat|nas"),
        Setup = ["Nothing to set on the server. In Synology Chat open **Integration → Incoming Webhooks**, create one for a channel and copy its URL. The Daksha server must be able to reach your NAS."],
        Notes = "Incoming webhooks can only send, so Daksha posts but cannot read the channel.",
        Credentials = new()
        {
            Fields = [F("webhook", "Webhook address", "url", "https://nas.example.com:5001/webapi/entry.cgi?api=SYNO.Chat.External&method=incoming&version=2&token=…")],
            Prepare = async (v, ct) => { var uri = await HostGuard.CheckAsync(v["webhook"], allowHttp: true, ct); if (!uri.Query.Contains("SYNO.Chat.External", StringComparison.Ordinal)) throw new AppException(400, "That does not look like a Synology Chat incoming webhook."); v["webhook"] = uri.ToString(); },
            Probe = c => Task.FromResult(new ProfileInfo(new Uri(c.Cred["webhook"]).Authority, "Synology Chat on " + new Uri(c.Cred["webhook"]).Host)),
        },
        Views = [new ViewSpec { Id = "channel", Label = "Channel", Dashboard = true, Load = (c, _) => Task.FromResult(Info("webhook", "Incoming webhook", "Posts to " + new Uri(c.Cred["webhook"]).Host)) }],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Post a message", Inputs = [F("text", "Message", "textarea")],
                Run = async (c, i) => { await c.RawAsync(HttpMethod.Post, c.Cred["webhook"], "payload=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { text = i["text"] })), "application/x-www-form-urlencoded"); return new ActionResult("Posted."); } },
        ],
    };
}
