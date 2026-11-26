using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Social apps: YouTube, Instagram, Facebook Pages, X, LinkedIn, TikTok, Reddit, Pinterest.
// Each is read-only until the person turns on full access (which, where the provider has a separate permission, asks them to approve it).

public static partial class ProviderApps
{
    private static Regex Rx(string words) => new($@"\b({words})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static IDictionary<string, string> Bearer(string token) => new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

    private static string Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ============================================================================================
    // YouTube (Google sign-in, its own consent so Gmail's scopes are untouched)
    // ============================================================================================
    private const string YtApi = "https://www.googleapis.com/youtube/v3/";
    private static readonly Regex YtId = new(@"(?:v=|youtu\.be/|shorts/|embed/)([A-Za-z0-9_-]{11})|^([A-Za-z0-9_-]{11})$", RegexOptions.Compiled);

    public static string? YoutubeVideoId(string input) => YtId.Match(input.Trim()) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;

    private static AppItem YtVideo(JsonElement v)
    {
        var id = J.First(v, "id.videoId", "snippet.resourceId.videoId", "contentDetails.videoId", "id") ?? "";
        if (id.Length == 0) return null!;
        var views = J.S(v, "statistics.viewCount");
        return new AppItem(id, J.S(v, "snippet.title") ?? id, J.First(v, "snippet.videoOwnerChannelTitle", "snippet.channelTitle") is { Length: > 0 } c ? $"{c} · {J.When(J.S(v, "snippet.publishedAt"))}" : J.When(J.S(v, "snippet.publishedAt")),
            $"https://www.youtube.com/watch?v={id}", views is null ? null : $"{long.Parse(views):N0} views", J.First(v, "snippet.thumbnails.medium.url", "snippet.thumbnails.default.url"));
    }

    private static ProviderApp YouTube() => new()
    {
        Key = "youtube", Name = "YouTube", Icon = "SiYoutube", Color = "#FF0000", Account = "Subscriptions, playlists and videos",
        ConnectTitle = "Connect your YouTube account", ConnectLabel = "Continue with Google",
        ConnectBlurb = "Sign in with Google and Daksha can show your subscriptions, playlists and liked videos, and search YouTube. It never sees your Google password.",
        WriteLabel = "Allow Daksh to manage playlists", WriteHint = "Full access lets Daksha create playlists, add videos to them and like videos.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#youtube",
        Ask = Rx("youtube|playlists?|subscriptions?|watch later|videos?"),
        OAuth = new()
        {
            Section = "YouTube", Fallbacks = ["Gmail", "Google"],
            AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
            ReadScopes = ["https://www.googleapis.com/auth/youtube.readonly"], WriteScopes = ["https://www.googleapis.com/auth/youtube"],
            AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
        },
        Profile = async c =>
        {
            using var doc = await c.GetAsync($"{YtApi}channels?part=snippet&mine=true");
            var ch = J.Arr(doc.RootElement, "items").FirstOrDefault();
            if (ch.ValueKind != JsonValueKind.Object) throw new AppException(400, "This Google account has no YouTube channel yet.");
            return new ProfileInfo(J.S(ch, "id") ?? "", J.S(ch, "snippet.title") ?? "YouTube channel");
        },
        Views =
        [
            new ViewSpec { Id = "playlists", Label = "Playlists", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{YtApi}playlists?part=snippet,contentDetails&mine=true&maxResults=50");
                return J.Items(d.RootElement, "items", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "snippet.title") ?? "Playlist", $"{J.S(p, "contentDetails.itemCount") ?? "0"} videos", $"https://www.youtube.com/playlist?list={J.S(p, "id")}", null, J.First(p, "snippet.thumbnails.medium.url", "snippet.thumbnails.default.url"), "playlist-items")); } },
            new ViewSpec { Id = "playlist-items", Label = "Playlist", Hidden = true, TakesArg = true, Load = async (c, i) => { using var d = await c.GetAsync($"{YtApi}playlistItems?part=snippet&maxResults=50&playlistId={J.E(i["arg"])}");
                return J.Items(d.RootElement, "items", YtVideo); } },
            new ViewSpec { Id = "liked", Label = "Liked videos", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{YtApi}videos?part=snippet,statistics&myRating=like&maxResults=25");
                return J.Items(d.RootElement, "items", YtVideo); } },
            new ViewSpec { Id = "subscriptions", Label = "Subscriptions", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{YtApi}subscriptions?part=snippet&mine=true&maxResults=50&order=alphabetical");
                return J.Items(d.RootElement, "items", s => new AppItem(J.S(s, "snippet.resourceId.channelId") ?? "", J.S(s, "snippet.title") ?? "Channel", J.Clip(J.S(s, "snippet.description"), 100), $"https://www.youtube.com/channel/{J.S(s, "snippet.resourceId.channelId")}", null, J.S(s, "snippet.thumbnails.default.url"))); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [new("q", "Search YouTube", Placeholder: "What do you want to watch?")], Load = async (c, i) => { using var d = await c.GetAsync($"{YtApi}search?part=snippet&type=video&maxResults=15&q={J.E(i["q"])}");
                return J.Items(d.RootElement, "items", YtVideo); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "add-to-playlist", Label = "Add a video to a playlist", Inputs = [new("playlist", "Playlist", "select-view", OptionsView: "playlists"), new("video", "Video link", Placeholder: "https://youtu.be/…")],
                Run = async (c, i) => { var vid = YoutubeVideoId(i["video"]) ?? throw new AppException(400, "That does not look like a YouTube video link.");
                    using var _ = await c.PostAsync($"{YtApi}playlistItems?part=snippet", new { snippet = new { playlistId = i["playlist"], resourceId = new { kind = "youtube#video", videoId = vid } } });
                    return new ActionResult("Added to the playlist.", $"https://www.youtube.com/playlist?list={i["playlist"]}"); } },
            new ActionSpec { Id = "create-playlist", Label = "Create a playlist", Inputs = [new("title", "Title"), new("description", "Description", "textarea", Required: false), new("privacy", "Who can see it", "select", Options: ["private", "unlisted", "public"], Default: "private")],
                Run = async (c, i) => { using var d = await c.PostAsync($"{YtApi}playlists?part=snippet,status", new { snippet = new { title = i["title"], description = i["description"] }, status = new { privacyStatus = i["privacy"] } });
                    return new ActionResult("Playlist created.", $"https://www.youtube.com/playlist?list={J.S(d.RootElement, "id")}"); } },
            new ActionSpec { Id = "rate", Label = "Like a video", Inputs = [new("video", "Video link", Placeholder: "https://youtu.be/…"), new("rating", "Rating", "select", Options: ["like", "none"], Default: "like")],
                Run = async (c, i) => { var vid = YoutubeVideoId(i["video"]) ?? throw new AppException(400, "That does not look like a YouTube video link.");
                    using var _ = await c.PostAsync($"{YtApi}videos/rate?id={vid}&rating={i["rating"]}"); return new ActionResult(i["rating"] == "like" ? "Liked." : "Rating removed."); } },
        ],
    };

    // ============================================================================================
    // Instagram (Instagram API with Instagram Login: business or creator accounts)
    // ============================================================================================
    private const string IgApi = "https://graph.instagram.com/v21.0/";

    private static ProviderApp Instagram() => new()
    {
        Key = "instagram-social", Name = "Instagram", Icon = "SiInstagram", Color = "#E4405F", Account = "Posts, insights, comments and messages",
        ConnectTitle = "Connect your Instagram account", ConnectLabel = "Continue with Instagram",
        ConnectBlurb = "Works with Instagram business and creator accounts. Daksha can show your posts, insights and comments. It never sees your Instagram password.",
        WriteLabel = "Allow Daksh to post and reply", WriteHint = "Full access lets Daksha publish photos, reply to comments and send direct messages, and read your messages.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#instagram",
        Ask = Rx("instagram|insta|reels?|followers"),
        OAuth = new()
        {
            Section = "Instagram", AuthUrl = "https://www.instagram.com/oauth/authorize", TokenUrl = "https://api.instagram.com/oauth/access_token", ScopeSeparator = ",",
            ReadScopes = ["instagram_business_basic", "instagram_business_manage_insights"],
            WriteScopes = ["instagram_business_content_publish", "instagram_business_manage_comments", "instagram_business_manage_messages"],
            AuthExtra = new() { ["enable_fb_login"] = "0", ["force_authentication"] = "1" },
            Exchange = async x =>
            {
                var code = x.Code.Split('#')[0];
                using var first = await ProviderEngine.FetchAsync(x.Clients, HttpMethod.Post, "https://api.instagram.com/oauth/access_token",
                    new Dictionary<string, string> { ["client_id"] = x.Config.ClientId, ["client_secret"] = x.Config.ClientSecret ?? "", ["grant_type"] = "authorization_code", ["redirect_uri"] = x.Config.RedirectUri, ["code"] = code }, x.Ct);
                var shortToken = first is null ? null : J.First(first.RootElement, "access_token", "data[0].access_token");
                if (shortToken is null) return null;
                using var longLived = await ProviderEngine.FetchAsync(x.Clients, HttpMethod.Get,
                    $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={J.E(x.Config.ClientSecret)}&access_token={J.E(shortToken)}", null, x.Ct);
                var token = longLived is null ? null : J.S(longLived.RootElement, "access_token");
                if (token is null) return null;
                var seconds = int.TryParse(J.S(longLived!.RootElement, "expires_in"), out var s) ? s : 5_184_000;
                // Renewed a week before it would run out (Instagram only renews tokens that are still valid).
                return new TokenSet(token, null, DateTimeOffset.UtcNow.AddSeconds(Math.Max(3600, seconds - 7 * 86400)));
            },
            Refresh = async (c, t) =>
            {
                using var d = await ProviderEngine.FetchAsync(c.Clients, HttpMethod.Get, $"https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token={J.E(t.Access)}", null, c.Ct);
                var token = d is null ? null : J.S(d.RootElement, "access_token");
                if (token is null) return null;
                var seconds = int.TryParse(J.S(d!.RootElement, "expires_in"), out var s) ? s : 5_184_000;
                return new TokenSet(token, null, DateTimeOffset.UtcNow.AddSeconds(Math.Max(3600, seconds - 7 * 86400)));
            },
        },
        Profile = async c =>
        {
            using var d = await c.GetAsync($"{IgApi}me?fields=user_id,username,account_type");
            var id = J.First(d.RootElement, "user_id", "id") ?? throw new AppException(502, "Instagram did not return the account.");
            return new ProfileInfo(id, "@" + (J.S(d.RootElement, "username") ?? id), new() { ["accountType"] = J.S(d.RootElement, "account_type") ?? "" });
        },
        Views =
        [
            new ViewSpec { Id = "media", Label = "Posts", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{IgApi}me/media?fields=id,caption,media_type,permalink,thumbnail_url,media_url,timestamp,like_count,comments_count&limit=25");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.Clip(J.S(m, "caption"), 90) is { Length: > 0 } t ? t : J.S(m, "media_type") ?? "Post",
                    $"{J.When(J.S(m, "timestamp"))} · {J.S(m, "like_count") ?? "0"} likes · {J.S(m, "comments_count") ?? "0"} comments", J.S(m, "permalink"), J.S(m, "media_type"), J.First(m, "thumbnail_url", "media_url"), "comments")); } },
            new ViewSpec { Id = "comments", Label = "Comments", Hidden = true, TakesArg = true, Load = async (c, i) => { using var d = await c.GetAsync($"{IgApi}{J.E(i["arg"])}/comments?fields=id,text,username,timestamp,like_count&limit=50");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "text") ?? "", $"@{J.S(m, "username")} · {J.When(J.S(m, "timestamp"))}")); } },
            new ViewSpec { Id = "insights", Label = "Insights", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{IgApi}me/insights?metric=reach,profile_views,accounts_engaged&metric_type=total_value&period=day");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "name") ?? "", J.S(m, "title") ?? J.S(m, "name") ?? "", J.S(m, "description"), null, J.S(m, "total_value.value"))); } },
            new ViewSpec { Id = "messages", Label = "Messages", Load = async (c, _) => { using var d = await c.GetAsync($"{IgApi}me/conversations?platform=instagram&fields=id,updated_time,participants&limit=25");
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", string.Join(", ", J.Arr(m, "participants.data").Select(p => "@" + J.S(p, "username"))), J.When(J.S(m, "updated_time")), null, null, null, "thread")); } },
            new ViewSpec { Id = "thread", Label = "Conversation", Hidden = true, TakesArg = true, Load = async (c, i) => { using var d = await c.GetAsync($"{IgApi}{J.E(i["arg"])}?fields=messages.limit(30){{message,from,created_time}}");
                return J.Items(d.RootElement, "messages.data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "message") ?? "(attachment)", $"@{J.S(m, "from.username")} · {J.When(J.S(m, "created_time"))}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "publish", Label = "Publish a photo", Hint = "The image must be a public JPEG link.", Inputs = [new("image", "Image link (JPEG)", "url", Placeholder: "https://…/photo.jpg"), new("caption", "Caption", "textarea", Required: false)],
                Run = async (c, i) => { using var made = await c.PostAsync($"{IgApi}me/media", new { image_url = i["image"], caption = i["caption"] });
                    var creation = J.S(made.RootElement, "id") ?? throw new AppException(502, "Instagram did not accept the image.");
                    using var pub = await c.PostAsync($"{IgApi}me/media_publish", new { creation_id = creation });
                    var id = J.S(pub.RootElement, "id");
                    string? link = null;
                    if (id is not null) { using var p = await c.GetAsync($"{IgApi}{J.E(id)}?fields=permalink"); link = J.S(p.RootElement, "permalink"); }
                    return new ActionResult("Published to Instagram.", link); } },
            new ActionSpec { Id = "reply", Label = "Reply to a comment", Inputs = [new("comment", "Comment id", Placeholder: "From the Comments list"), new("message", "Reply", "textarea")],
                Run = async (c, i) => { using var _ = await c.PostAsync($"{IgApi}{J.E(i["comment"])}/replies", new { message = i["message"] }); return new ActionResult("Reply sent."); } },
            new ActionSpec { Id = "message", Label = "Send a direct message", Hint = "Instagram only allows replies within 24 hours of the person's last message.", Inputs = [new("recipient", "Recipient id"), new("text", "Message", "textarea")],
                Run = async (c, i) => { using var _ = await c.PostAsync($"{IgApi}me/messages", new { recipient = new { id = i["recipient"] }, message = new { text = i["text"] } }); return new ActionResult("Message sent."); } },
        ],
    };

    // ============================================================================================
    // Facebook Pages
    // ============================================================================================
    private const string FbApi = "https://graph.facebook.com/v21.0/";

    private sealed record FbPage(string Id, string Name, string Token);

    private static async Task<FbPage> FbPageAsync(ProviderCtx c)
    {
        using var d = await c.GetAsync($"{FbApi}me/accounts?fields=id,name,access_token&limit=50");
        var pages = J.Arr(d.RootElement, "data").Select(p => new FbPage(J.S(p, "id") ?? "", J.S(p, "name") ?? "", J.S(p, "access_token") ?? "")).Where(p => p.Id.Length > 0 && p.Token.Length > 0).ToList();
        if (pages.Count == 0) throw new AppException(404, "No Facebook Pages were found. Allow Daksha to access a Page when you sign in, or create a Page first.");
        var want = c.Setting("pageName").Trim();
        return want.Length > 0 ? pages.FirstOrDefault(p => p.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || p.Id == want) ?? throw new AppException(404, $"No Page called “{want}” was found. Change the Page name in Manage.") : pages[0];
    }

    private static ProviderApp FacebookPages() => new()
    {
        Key = "facebook-pages", Name = "Facebook Pages", Icon = "SiFacebook", Color = "#0866FF", Account = "Page posts, comments and messages",
        ConnectTitle = "Connect your Facebook Pages", ConnectLabel = "Continue with Facebook",
        ConnectBlurb = "Sign in with Facebook and choose the Pages to share. Daksha can show their posts, comments and messages. It never sees your Facebook password.",
        WriteLabel = "Allow Daksh to post and reply", WriteHint = "Full access lets Daksha publish Page posts, reply to comments and send Page messages.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#facebook-pages",
        Ask = Rx("facebook|fb page|my page|page posts?"),
        ExtraSettings = [new("pageName", "Which Page", "text", "Part of the Page name, or its id. Leave empty to use the first Page.", "My business")],
        OAuth = new()
        {
            Section = "Facebook", AuthUrl = "https://www.facebook.com/v21.0/dialog/oauth", TokenUrl = "https://graph.facebook.com/v21.0/oauth/access_token", ScopeSeparator = ",",
            ReadScopes = ["pages_show_list", "pages_read_engagement", "pages_read_user_content", "read_insights"],
            WriteScopes = ["pages_manage_posts", "pages_manage_engagement", "pages_messaging"],
            Exchange = async x =>
            {
                // Posted as a form so the app secret and tokens never appear in a URL (and so never in a log).
                using var first = await ProviderEngine.FetchAsync(x.Clients, HttpMethod.Post, "https://graph.facebook.com/v21.0/oauth/access_token",
                    new Dictionary<string, string> { ["client_id"] = x.Config.ClientId, ["client_secret"] = x.Config.ClientSecret ?? "", ["redirect_uri"] = x.Config.RedirectUri, ["code"] = x.Code }, x.Ct);
                var shortToken = first is null ? null : J.S(first.RootElement, "access_token");
                if (shortToken is null) return null;
                using var longLived = await ProviderEngine.FetchAsync(x.Clients, HttpMethod.Post, "https://graph.facebook.com/v21.0/oauth/access_token",
                    new Dictionary<string, string> { ["grant_type"] = "fb_exchange_token", ["client_id"] = x.Config.ClientId, ["client_secret"] = x.Config.ClientSecret ?? "", ["fb_exchange_token"] = shortToken }, x.Ct);
                var token = longLived is null ? null : J.S(longLived.RootElement, "access_token");
                if (token is null) return null;
                var seconds = int.TryParse(J.S(longLived!.RootElement, "expires_in"), out var s) ? s : 5_184_000;
                return new TokenSet(token, null, DateTimeOffset.UtcNow.AddSeconds(Math.Max(3600, seconds - 86400)));
            },
        },
        Profile = async c => { using var d = await c.GetAsync($"{FbApi}me?fields=id,name"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.S(d.RootElement, "name") ?? "Facebook"); },
        Views =
        [
            new ViewSpec { Id = "posts", Label = "Page posts", Dashboard = true, Load = async (c, _) => { var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{p.Id}/posts?fields=id,message,created_time,permalink_url,reactions.summary(true).limit(0),comments.summary(true).limit(0)&limit=25", Bearer(p.Token));
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.Clip(J.S(m, "message"), 100) is { Length: > 0 } t ? t : "(post without text)",
                    $"{p.Name} · {J.When(J.S(m, "created_time"))} · {J.S(m, "reactions.summary.total_count") ?? "0"} reactions · {J.S(m, "comments.summary.total_count") ?? "0"} comments", J.S(m, "permalink_url"), null, null, "post-comments")); } },
            new ViewSpec { Id = "post-comments", Label = "Comments", Hidden = true, TakesArg = true, Load = async (c, i) => { var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{J.E(i["arg"])}/comments?fields=id,message,from,created_time&limit=50", Bearer(p.Token));
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "message") ?? "", $"{J.S(m, "from.name") ?? "Someone"} · {J.When(J.S(m, "created_time"))}")); } },
            new ViewSpec { Id = "overview", Label = "Page overview", Dashboard = true, Load = async (c, _) => { var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{p.Id}?fields=name,fan_count,followers_count,category,link", Bearer(p.Token));
                var r = d.RootElement; return (IReadOnlyList<AppItem>)[new AppItem("followers", p.Name, J.S(r, "category"), J.S(r, "link"), $"{J.First(r, "followers_count", "fan_count") ?? "0"} followers")]; } },
            new ViewSpec { Id = "messages", Label = "Messages", Load = async (c, _) => { var p = await FbPageAsync(c);
                using var d = await c.GetAsync($"{FbApi}{p.Id}/conversations?fields=id,snippet,updated_time,senders&limit=25", Bearer(p.Token));
                return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.Clip(J.S(m, "snippet"), 100), $"{J.S(m, "senders.data[0].name") ?? "Someone"} · {J.When(J.S(m, "updated_time"))}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Publish a post", Inputs = [new("message", "What do you want to say?", "textarea"), new("link", "Link", "url", Required: false)],
                Run = async (c, i) => { var p = await FbPageAsync(c); using var d = await c.PostAsync($"{FbApi}{p.Id}/feed", new { message = i["message"], link = i["link"].Length > 0 ? i["link"] : null }, Bearer(p.Token));
                    return new ActionResult($"Posted to {p.Name}.", $"https://www.facebook.com/{J.S(d.RootElement, "id")}"); } },
            new ActionSpec { Id = "reply", Label = "Reply to a comment", Inputs = [new("comment", "Comment id", Placeholder: "From the Comments list"), new("message", "Reply", "textarea")],
                Run = async (c, i) => { var p = await FbPageAsync(c); using var _ = await c.PostAsync($"{FbApi}{J.E(i["comment"])}/comments", new { message = i["message"] }, Bearer(p.Token)); return new ActionResult("Reply posted."); } },
        ],
    };

    // ============================================================================================
    // X
    // ============================================================================================
    private const string XApi = "https://api.twitter.com/2/";
    private static readonly Regex XStatus = new(@"status/(\d{5,25})|^(\d{5,25})$", RegexOptions.Compiled);
    private const string XFields = "tweet.fields=created_at,public_metrics&expansions=author_id&user.fields=username";

    private static IReadOnlyList<AppItem> XTweets(JsonElement root)
    {
        var users = J.Arr(root, "includes.users").ToDictionary(u => J.S(u, "id") ?? "", u => J.S(u, "username") ?? "", StringComparer.Ordinal);
        return J.Items(root, "data", t =>
        {
            var id = J.S(t, "id") ?? ""; var author = users.GetValueOrDefault(J.S(t, "author_id") ?? "", "i");
            return new AppItem(id, J.S(t, "text") ?? "", $"@{author} · {J.When(J.S(t, "created_at"))} · {J.S(t, "public_metrics.like_count") ?? "0"} likes", $"https://x.com/{author}/status/{id}");
        });
    }

    private static ProviderApp X() => new()
    {
        Key = "x", Name = "X", Icon = "SiX", Color = "#000000", Account = "Timeline, mentions and posts",
        ConnectTitle = "Connect your X account", ConnectLabel = "Continue with X",
        ConnectBlurb = "Sign in with X and Daksha can show your timeline, mentions, posts and bookmarks, and search recent posts. Some lists need a paid X API plan.",
        WriteLabel = "Allow Daksh to post and like", WriteHint = "Full access lets Daksha publish posts, reply and like.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#x-twitter",
        Ask = Rx("twitter|tweets?|x\\.com|my timeline|mentions?"),
        OAuth = new()
        {
            Section = "X", AuthUrl = "https://x.com/i/oauth2/authorize", TokenUrl = "https://api.twitter.com/2/oauth2/token", Pkce = true, BasicAuth = true,
            ReadScopes = ["tweet.read", "users.read", "offline.access", "bookmark.read"], WriteScopes = ["tweet.write", "like.write"],
        },
        Profile = async c => { using var d = await c.GetAsync($"{XApi}users/me?user.fields=username"); return new ProfileInfo(J.S(d.RootElement, "data.id") ?? "", "@" + (J.S(d.RootElement, "data.username") ?? "x")); },
        Views =
        [
            new ViewSpec { Id = "mentions", Label = "Mentions", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{XApi}users/{c.Account.AccountId}/mentions?max_results=20&{XFields}"); return XTweets(d.RootElement); } },
            new ViewSpec { Id = "timeline", Label = "Timeline", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{XApi}users/{c.Account.AccountId}/timelines/reverse_chronological?max_results=20&{XFields}"); return XTweets(d.RootElement); } },
            new ViewSpec { Id = "posts", Label = "My posts", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{XApi}users/{c.Account.AccountId}/tweets?max_results=20&exclude=retweets&{XFields}"); return XTweets(d.RootElement); } },
            new ViewSpec { Id = "bookmarks", Label = "Bookmarks", Load = async (c, _) => { using var d = await c.GetAsync($"{XApi}users/{c.Account.AccountId}/bookmarks?max_results=20&{XFields}"); return XTweets(d.RootElement); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [new("q", "Search recent posts", Placeholder: "from:someone OR a topic")], Load = async (c, i) => { using var d = await c.GetAsync($"{XApi}tweets/search/recent?max_results=15&query={J.E(i["q"])}&{XFields}"); return XTweets(d.RootElement); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Publish a post", Inputs = [new("text", "What is happening?", "textarea")],
                Run = async (c, i) => { if (i["text"].Length > 280) throw new AppException(400, "Posts on X can be at most 280 characters.");
                    using var d = await c.PostAsync($"{XApi}tweets", new { text = i["text"] }); return new ActionResult("Posted.", $"https://x.com/i/web/status/{J.S(d.RootElement, "data.id")}"); } },
            new ActionSpec { Id = "reply", Label = "Reply to a post", Inputs = [new("post", "Post link", Placeholder: "https://x.com/…/status/123…"), new("text", "Reply", "textarea")],
                Run = async (c, i) => { var m = XStatus.Match(i["post"]); if (!m.Success) throw new AppException(400, "That does not look like a post link."); var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (i["text"].Length > 280) throw new AppException(400, "Posts on X can be at most 280 characters.");
                    using var d = await c.PostAsync($"{XApi}tweets", new { text = i["text"], reply = new { in_reply_to_tweet_id = id } }); return new ActionResult("Reply posted.", $"https://x.com/i/web/status/{J.S(d.RootElement, "data.id")}"); } },
            new ActionSpec { Id = "like", Label = "Like a post", Inputs = [new("post", "Post link", Placeholder: "https://x.com/…/status/123…")],
                Run = async (c, i) => { var m = XStatus.Match(i["post"]); if (!m.Success) throw new AppException(400, "That does not look like a post link.");
                    using var _ = await c.PostAsync($"{XApi}users/{c.Account.AccountId}/likes", new { tweet_id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value }); return new ActionResult("Liked."); } },
        ],
    };

    // ============================================================================================
    // LinkedIn (Sign In with LinkedIn + Share on LinkedIn; reading the feed is not open to ordinary apps)
    // ============================================================================================
    private static void LinkedInHeaders(HttpRequestMessage r, ProviderCtx c)
    {
        r.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        r.Headers.TryAddWithoutValidation("LinkedIn-Version", c.Server("LinkedIn:ApiVersion") is { Length: > 0 } v ? v : "202510");
    }

    private static ProviderApp LinkedIn() => new()
    {
        Key = "linkedin", Name = "LinkedIn", Icon = "mono:in", Color = "#0A66C2", Account = "Your profile and posts",
        ConnectTitle = "Connect your LinkedIn account", ConnectLabel = "Continue with LinkedIn",
        ConnectBlurb = "Sign in with LinkedIn and Daksha can show your profile and publish posts for you. LinkedIn does not let ordinary apps read your feed or messages.",
        WriteLabel = "Allow Daksh to post", WriteHint = "Full access lets Daksha publish posts to your LinkedIn profile.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#linkedin",
        Ask = Rx("linkedin"),
        Decorate = LinkedInHeaders,
        OAuth = new()
        {
            Section = "LinkedIn", AuthUrl = "https://www.linkedin.com/oauth/v2/authorization", TokenUrl = "https://www.linkedin.com/oauth/v2/accessToken",
            ReadScopes = ["openid", "profile", "email"], WriteScopes = ["w_member_social"],
        },
        Profile = async c =>
        {
            using var d = await c.GetAsync("https://api.linkedin.com/v2/userinfo");
            var sub = J.S(d.RootElement, "sub") ?? throw new AppException(502, "LinkedIn did not return the profile.");
            return new ProfileInfo(sub, J.S(d.RootElement, "name") ?? "LinkedIn member", new() { ["email"] = J.S(d.RootElement, "email") ?? "" });
        },
        Views =
        [
            new ViewSpec { Id = "profile", Label = "Profile", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://api.linkedin.com/v2/userinfo");
                return (IReadOnlyList<AppItem>)[new AppItem(c.Account.AccountId, J.S(d.RootElement, "name") ?? "", J.S(d.RootElement, "email"), "https://www.linkedin.com/in/me/", null, J.S(d.RootElement, "picture"))]; } },
            new ViewSpec { Id = "posts", Label = "My posts", Empty = "LinkedIn only lets approved apps read posts. Publishing still works with full access.", Load = async (c, _) => { using var d = await c.GetAsync($"https://api.linkedin.com/rest/posts?author={J.E("urn:li:person:" + c.Account.AccountId)}&q=author&count=20");
                return J.Items(d.RootElement, "elements", p => new AppItem(J.S(p, "id") ?? "", J.Clip(J.S(p, "commentary"), 140), J.When(J.S(p, "publishedAt")), $"https://www.linkedin.com/feed/update/{J.S(p, "id")}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Publish a post", Inputs = [new("text", "What do you want to share?", "textarea"), new("link", "Link", "url", Required: false)],
                Run = async (c, i) =>
                {
                    if (i["text"].Length > 3000) throw new AppException(400, "LinkedIn posts can be at most 3,000 characters.");
                    object body = i["link"].Length > 0
                        ? new { author = $"urn:li:person:{c.Account.AccountId}", commentary = i["text"], visibility = "PUBLIC", distribution = new { feedDistribution = "MAIN_FEED", targetEntities = Array.Empty<string>(), thirdPartyDistributionChannels = Array.Empty<string>() }, content = new { article = new { source = i["link"], title = J.Clip(i["text"], 80) } }, lifecycleState = "PUBLISHED", isReshareDisabledByAuthor = false }
                        : new { author = $"urn:li:person:{c.Account.AccountId}", commentary = i["text"], visibility = "PUBLIC", distribution = new { feedDistribution = "MAIN_FEED", targetEntities = Array.Empty<string>(), thirdPartyDistributionChannels = Array.Empty<string>() }, lifecycleState = "PUBLISHED", isReshareDisabledByAuthor = false };
                    using var _ = await c.PostAsync("https://api.linkedin.com/rest/posts", body);
                    return new ActionResult("Posted to LinkedIn.", "https://www.linkedin.com/feed/");
                } },
        ],
    };

    // ============================================================================================
    // TikTok
    // ============================================================================================
    private const string TtApi = "https://open.tiktokapis.com/v2/";

    private static ProviderApp TikTok() => new()
    {
        Key = "tiktok", Name = "TikTok", Icon = "SiTiktok", Color = "#000000", Account = "Your videos and stats",
        ConnectTitle = "Connect your TikTok account", ConnectLabel = "Continue with TikTok",
        ConnectBlurb = "Sign in with TikTok and Daksha can show your profile stats and your videos with their views and likes.",
        WriteLabel = "Allow Daksh to send videos to drafts", WriteHint = "Full access lets Daksha send a video (from a link you own) to your TikTok drafts, where you finish and publish it in the app.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#tiktok",
        Ask = Rx("tiktok"),
        OAuth = new()
        {
            Section = "TikTok", AuthUrl = "https://www.tiktok.com/v2/auth/authorize/", TokenUrl = "https://open.tiktokapis.com/v2/oauth/token/", ClientIdParam = "client_key", ScopeSeparator = ",",
            ReadScopes = ["user.info.basic", "user.info.stats", "video.list"], WriteScopes = ["video.upload"], TokenExtras = ["open_id"],
        },
        Profile = async c =>
        {
            using var d = await c.GetAsync($"{TtApi}user/info/?fields=open_id,display_name");
            var id = J.S(d.RootElement, "data.user.open_id") ?? c.Extra("open_id");
            if (id.Length == 0) throw new AppException(502, "TikTok did not return the profile.");
            return new ProfileInfo(id, J.S(d.RootElement, "data.user.display_name") ?? "TikTok");
        },
        Views =
        [
            new ViewSpec { Id = "videos", Label = "Videos", Dashboard = true, Load = async (c, _) => { using var d = await c.PostAsync($"{TtApi}video/list/?fields=id,title,share_url,view_count,like_count,comment_count,create_time,cover_image_url", new { max_count = 20 });
                return J.Items(d.RootElement, "data.videos", v => new AppItem(J.S(v, "id") ?? "", J.Clip(J.S(v, "title"), 90) is { Length: > 0 } t ? t : "Video", $"{J.When(J.S(v, "create_time"))} · {J.S(v, "like_count") ?? "0"} likes · {J.S(v, "comment_count") ?? "0"} comments", J.S(v, "share_url"), $"{J.S(v, "view_count") ?? "0"} views", J.S(v, "cover_image_url"))); } },
            new ViewSpec { Id = "stats", Label = "Profile stats", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{TtApi}user/info/?fields=display_name,follower_count,following_count,likes_count,video_count");
                var u = J.At(d.RootElement, "data.user");
                return (IReadOnlyList<AppItem>)[new AppItem("followers", "Followers", null, null, J.S(u, "follower_count") ?? "0"), new AppItem("likes", "Likes", null, null, J.S(u, "likes_count") ?? "0"), new AppItem("videos", "Videos", null, null, J.S(u, "video_count") ?? "0"), new AppItem("following", "Following", null, null, J.S(u, "following_count") ?? "0")]; } },
        ],
        Actions =
        [
            new ActionSpec { Id = "upload", Label = "Send a video to my drafts", Hint = "TikTok must be able to download the video from your verified domain.", Inputs = [new("url", "Video link (MP4)", "url", Placeholder: "https://your-domain/video.mp4")],
                Run = async (c, i) => { using var _ = await c.PostAsync($"{TtApi}post/publish/inbox/video/init/", new { source_info = new { source = "PULL_FROM_URL", video_url = i["url"] } });
                    return new ActionResult("Sent. Open TikTok to finish and publish the draft."); } },
        ],
    };

    // ============================================================================================
    // Reddit
    // ============================================================================================
    private const string RdApi = "https://oauth.reddit.com/";
    private static readonly Regex Subreddit = new(@"^[A-Za-z0-9_]{2,21}$", RegexOptions.Compiled);
    private static readonly Regex Fullname = new(@"^t[1-6]_[a-z0-9]{2,12}$", RegexOptions.Compiled);

    private static AppItem RdPost(JsonElement child)
    {
        var p = J.At(child, "data");
        var id = J.S(p, "id") ?? "";
        var thumb = J.S(p, "thumbnail");
        return new AppItem(id, J.S(p, "title") ?? "", $"r/{J.S(p, "subreddit")} · u/{J.S(p, "author")} · {J.S(p, "score") ?? "0"} points · {J.S(p, "num_comments") ?? "0"} comments",
            $"https://www.reddit.com{J.S(p, "permalink")}", null, thumb is { } t && t.StartsWith("http", StringComparison.Ordinal) ? t : null, "thread");
    }

    private static async Task<string> RdSubmitAsync(ProviderCtx c, string path, Dictionary<string, string> form)
    {
        form["api_type"] = "json";
        using var d = await c.PostFormAsync($"{RdApi}{path}", form);
        var err = J.Arr(d.RootElement, "json.errors").FirstOrDefault();
        if (err.ValueKind == JsonValueKind.Array && err.GetArrayLength() > 1) throw new AppException(400, $"Reddit said: {err[1].GetString()}");
        return J.S(d.RootElement, "json.data.url") ?? "";
    }

    private static ProviderApp Reddit() => new()
    {
        Key = "reddit", Name = "Reddit", Icon = "SiReddit", Color = "#FF4500", Account = "Your feed, subreddits and saved posts",
        ConnectTitle = "Connect your Reddit account", ConnectLabel = "Continue with Reddit",
        ConnectBlurb = "Sign in with Reddit and Daksha can show your front page, subscribed subreddits and saved posts, and search Reddit. It never sees your Reddit password.",
        WriteLabel = "Allow Daksh to post, comment and vote", WriteHint = "Full access lets Daksha submit posts, write comments, vote and read your inbox.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#reddit",
        Ask = Rx("reddit|subreddits?|r/\\w+"),
        Decorate = (r, c) => { r.Headers.UserAgent.Clear(); r.Headers.TryAddWithoutValidation("User-Agent", c.Server("Reddit:UserAgent") is { Length: > 0 } ua ? ua : "web:daksha-personal-assistant:1.0 (by /u/daksha)"); },
        OAuth = new()
        {
            Section = "Reddit", AuthUrl = "https://www.reddit.com/api/v1/authorize", TokenUrl = "https://www.reddit.com/api/v1/access_token", BasicAuth = true, AuthExtra = new() { ["duration"] = "permanent" },
            ReadScopes = ["identity", "mysubreddits", "read", "history"], WriteScopes = ["submit", "edit", "vote", "privatemessages"],
        },
        Profile = async c => { using var d = await c.GetAsync($"{RdApi}api/v1/me?raw_json=1"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", "u/" + (J.S(d.RootElement, "name") ?? "reddit"), new() { ["username"] = J.S(d.RootElement, "name") ?? "" }); },
        Views =
        [
            new ViewSpec { Id = "home", Label = "Front page", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{RdApi}best?limit=25&raw_json=1"); return J.Items(d.RootElement, "data.children", RdPost); } },
            new ViewSpec { Id = "thread", Label = "Comments", Hidden = true, TakesArg = true, Load = async (c, i) =>
                { if (!Regex.IsMatch(i["arg"], "^[a-z0-9]{2,12}$")) throw new AppException(400, "That post is not valid.");
                  using var d = await c.GetAsync($"{RdApi}comments/{i["arg"]}?limit=40&depth=1&raw_json=1");
                  var listing = d.RootElement.ValueKind == JsonValueKind.Array && d.RootElement.GetArrayLength() > 1 ? d.RootElement[1] : default;
                  return J.Items(listing, "data.children", ch => J.S(ch, "kind") == "t1" ? new AppItem(J.S(ch, "data.id") ?? "", J.S(ch, "data.body") ?? "", $"u/{J.S(ch, "data.author")} · {J.S(ch, "data.score") ?? "0"} points") : null); } },
            new ViewSpec { Id = "saved", Label = "Saved", Load = async (c, _) => { using var d = await c.GetAsync($"{RdApi}user/{J.E(c.Extra("username"))}/saved?limit=25&raw_json=1");
                return J.Items(d.RootElement, "data.children", ch => J.S(ch, "kind") == "t3" ? RdPost(ch) : new AppItem(J.S(ch, "data.id") ?? "", J.Clip(J.S(ch, "data.body"), 120), $"u/{J.S(ch, "data.author")} · comment", $"https://www.reddit.com{J.S(ch, "data.permalink")}")); } },
            new ViewSpec { Id = "subscriptions", Label = "Subreddits", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{RdApi}subreddits/mine/subscriber?limit=50&raw_json=1");
                return J.Items(d.RootElement, "data.children", ch => new AppItem(J.S(ch, "data.id") ?? "", $"r/{J.S(ch, "data.display_name")}", J.Clip(J.S(ch, "data.public_description") ?? J.S(ch, "data.title"), 90), $"https://www.reddit.com{J.S(ch, "data.url")}", $"{J.S(ch, "data.subscribers") ?? "0"} members")); } },
            new ViewSpec { Id = "inbox", Label = "Inbox", Load = async (c, _) => { using var d = await c.GetAsync($"{RdApi}message/inbox?limit=25&raw_json=1");
                return J.Items(d.RootElement, "data.children", ch => new AppItem(J.S(ch, "data.name") ?? "", J.S(ch, "data.subject") ?? "(no subject)", $"u/{J.S(ch, "data.author")} · {J.When(J.S(ch, "data.created_utc")?.Split('.')[0])}", null, J.S(ch, "data.new") == "true" ? "new" : null, null, null, J.S(ch, "data.body"))); } },
            new ViewSpec { Id = "subreddit", Label = "Browse a subreddit", Inputs = [new("sub", "Subreddit", Placeholder: "e.g. worldnews")], Load = async (c, i) =>
                { var sub = i["sub"].Trim().TrimStart('r', '/').TrimStart('/'); if (!Subreddit.IsMatch(sub)) throw new AppException(400, "Enter a subreddit name, like worldnews.");
                  using var d = await c.GetAsync($"{RdApi}r/{sub}/hot?limit=25&raw_json=1"); return J.Items(d.RootElement, "data.children", RdPost); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [new("q", "Search Reddit")], Load = async (c, i) => { using var d = await c.GetAsync($"{RdApi}search?q={J.E(i["q"])}&limit=20&type=link&raw_json=1"); return J.Items(d.RootElement, "data.children", RdPost); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "submit", Label = "Submit a text post", Inputs = [new("sub", "Subreddit", Placeholder: "e.g. test"), new("title", "Title"), new("text", "Text", "textarea", Required: false)],
                Run = async (c, i) => { var sub = i["sub"].Trim().TrimStart('r', '/').TrimStart('/'); if (!Subreddit.IsMatch(sub)) throw new AppException(400, "Enter a subreddit name, like test.");
                    var url = await RdSubmitAsync(c, "api/submit", new() { ["kind"] = "self", ["sr"] = sub, ["title"] = i["title"], ["text"] = i["text"], ["resubmit"] = "true" }); return new ActionResult("Submitted.", url.Length > 0 ? url : null); } },
            new ActionSpec { Id = "comment", Label = "Write a comment", Inputs = [new("thing", "Post or comment id", Placeholder: "t3_abc123"), new("text", "Comment", "textarea")],
                Run = async (c, i) => { if (!Fullname.IsMatch(i["thing"])) throw new AppException(400, "Use the full id, like t3_abc123 for a post."); await RdSubmitAsync(c, "api/comment", new() { ["thing_id"] = i["thing"], ["text"] = i["text"] }); return new ActionResult("Comment posted."); } },
            new ActionSpec { Id = "vote", Label = "Vote", Inputs = [new("thing", "Post or comment id", Placeholder: "t3_abc123"), new("dir", "Vote", "select", Options: ["1", "0", "-1"], Default: "1", Hint: "1 up, 0 clear, -1 down")],
                Run = async (c, i) => { if (!Fullname.IsMatch(i["thing"])) throw new AppException(400, "Use the full id, like t3_abc123 for a post."); using var _ = await c.PostFormAsync($"{RdApi}api/vote", new Dictionary<string, string> { ["id"] = i["thing"], ["dir"] = i["dir"] }); return new ActionResult("Vote recorded."); } },
        ],
    };

    // ============================================================================================
    // Pinterest
    // ============================================================================================
    private static string PinBase(ProviderCtx c) => c.Server("Pinterest:ApiBase") is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.pinterest.com/v5";

    private static ProviderApp Pinterest() => new()
    {
        Key = "pinterest", Name = "Pinterest", Icon = "SiPinterest", Color = "#BD081C", Account = "Boards and pins",
        ConnectTitle = "Connect your Pinterest account", ConnectLabel = "Continue with Pinterest",
        ConnectBlurb = "Sign in with Pinterest and Daksha can show your boards and pins. It never sees your Pinterest password.",
        WriteLabel = "Allow Daksh to create boards and pins", WriteHint = "Full access lets Daksha create boards and save pins.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#pinterest",
        Ask = Rx("pinterest|pins?|boards?"),
        OAuth = new()
        {
            Section = "Pinterest", AuthUrl = "https://www.pinterest.com/oauth/", TokenUrl = "https://api.pinterest.com/v5/oauth/token", BasicAuth = true, ScopeSeparator = ",",
            ReadScopes = ["user_accounts:read", "boards:read", "pins:read"], WriteScopes = ["boards:write", "pins:write"],
        },
        Profile = async c => { using var d = await c.GetAsync($"{PinBase(c)}/user_account"); var name = J.S(d.RootElement, "username") ?? throw new AppException(502, "Pinterest did not return the profile."); return new ProfileInfo(name, name); },
        Views =
        [
            new ViewSpec { Id = "boards", Label = "Boards", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{PinBase(c)}/boards?page_size=50");
                return J.Items(d.RootElement, "items", b => new AppItem(J.S(b, "id") ?? "", J.S(b, "name") ?? "Board", $"{J.S(b, "pin_count") ?? "0"} pins · {J.S(b, "privacy")?.ToLowerInvariant()}", null, null, null, "board-pins")); } },
            new ViewSpec { Id = "board-pins", Label = "Board", Hidden = true, TakesArg = true, Load = async (c, i) => { using var d = await c.GetAsync($"{PinBase(c)}/boards/{J.E(i["arg"])}/pins?page_size=50");
                return J.Items(d.RootElement, "items", p => new AppItem(J.S(p, "id") ?? "", J.First(p, "title", "description") is { Length: > 0 } t ? J.Clip(t, 90) : "Pin", J.First(p, "link", "created_at"), $"https://www.pinterest.com/pin/{J.S(p, "id")}/", null, J.First(p, "media.images.150x150.url", "media.images.400x300.url"))); } },
            new ViewSpec { Id = "pins", Label = "Recent pins", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{PinBase(c)}/pins?page_size=25");
                return J.Items(d.RootElement, "items", p => new AppItem(J.S(p, "id") ?? "", J.First(p, "title", "description") is { Length: > 0 } t ? J.Clip(t, 90) : "Pin", J.First(p, "link", "created_at"), $"https://www.pinterest.com/pin/{J.S(p, "id")}/", null, J.First(p, "media.images.150x150.url", "media.images.400x300.url"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-board", Label = "Create a board", Inputs = [new("name", "Board name"), new("description", "Description", "textarea", Required: false), new("privacy", "Who can see it", "select", Options: ["PUBLIC", "SECRET"], Default: "PUBLIC")],
                Run = async (c, i) => { using var d = await c.PostAsync($"{PinBase(c)}/boards", new { name = i["name"], description = i["description"], privacy = i["privacy"] }); return new ActionResult("Board created."); } },
            new ActionSpec { Id = "create-pin", Label = "Save a pin", Inputs = [new("board", "Board", "select-view", OptionsView: "boards"), new("image", "Image link", "url"), new("title", "Title", Required: false), new("description", "Description", "textarea", Required: false), new("link", "Destination link", "url", Required: false)],
                Run = async (c, i) => { using var d = await c.PostAsync($"{PinBase(c)}/pins", new { board_id = i["board"], title = i["title"], description = i["description"], link = i["link"].Length > 0 ? i["link"] : null, media_source = new { source_type = "image_url", url = i["image"] } });
                    return new ActionResult("Pin saved.", $"https://www.pinterest.com/pin/{J.S(d.RootElement, "id")}/"); } },
        ],
    };
}
