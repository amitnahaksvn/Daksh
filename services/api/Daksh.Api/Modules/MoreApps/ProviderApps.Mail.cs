using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Daksh.Api.Modules;

// Mail and calendar over open standards: Yahoo Mail and Proton Mail (IMAP and SMTP, with an app password or the Proton Bridge password) and
// Apple Calendar (CalDAV with an app-specific password).

public static partial class ProviderApps
{
    public static IReadOnlyList<ProviderApp> All => _all ??= [
        YouTube(), GoogleMaps(), Uber(), YahooMail(), ProtonMail(), AppleCalendar(),
        Instagram(), FacebookPages(), X(), LinkedIn(), TikTok(), Reddit(), Pinterest(),
        AmazonSellingPartner(), Shopify(), BookingCom(), QuickBooks(), HomeAssistant(), Irctc(), DigiLocker(),
        // Batch 2: developer, cloud and work tools
        GitLab(), Bitbucket(), Jira(), Sentry(), DockerHub(), Cloudflare(), Vercel(), Supabase(), Firebase(), N8n(), Airtable(), Confluence(),
        Razorpay(), Stripe(), PayPal(), ZohoMail(), Calendly(), Zoom(), Dropbox(), Box(),
        // Batch 3: cloud, commerce, accounting, smart home, automation
        Aws(), Azure(), GoogleCloud(), Kubernetes(), OnePassword(), Obsidian(), McpServers(),
        WooCommerce(), Ebay(), Xero(), ZohoBooks(), Mapbox(), Amadeus(), Mastodon(), Bluesky(), Zapier(), Make(), Ifttt(), Hue(), SmartThings(), Tuya(), Sonos(), Weather(), Bhashini(),
    ];
    private static IReadOnlyList<ProviderApp>? _all;

    public static ProviderApp Get(string key) => All.First(a => a.Key == key);

    // ============================================================================================
    // IMAP / SMTP
    // ============================================================================================
    private sealed record MailServer(string ImapHost, int ImapPort, SecureSocketOptions ImapSecurity, string SmtpHost, int SmtpPort, SecureSocketOptions SmtpSecurity, bool TrustLocalCertificate);

    private static MailServer MailServerFor(ProviderCtx c)
    {
        if (c.App.Key == "yahoo-mail") return new("imap.mail.yahoo.com", 993, SecureSocketOptions.SslOnConnect, "smtp.mail.yahoo.com", 465, SecureSocketOptions.SslOnConnect, false);
        var host = c.Cred.GetValueOrDefault("host") is { Length: > 0 } h ? h : "127.0.0.1";
        return new(host, int.TryParse(c.Cred.GetValueOrDefault("imapPort"), out var ip) ? ip : 1143, SecureSocketOptions.StartTls, host, int.TryParse(c.Cred.GetValueOrDefault("smtpPort"), out var sp) ? sp : 1025, SecureSocketOptions.StartTls, true);
    }

    private static bool IsPrivateHost(string host) =>
        host is "localhost" || (IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || ip.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..]));

    private static async Task<ImapClient> ImapAsync(ProviderCtx c)
    {
        var s = MailServerFor(c);
        var client = new ImapClient { Timeout = 20_000 };
        // Proton Bridge presents its own self-signed certificate on the same machine; only then is the certificate not checked.
        if (s.TrustLocalCertificate && IsPrivateHost(s.ImapHost)) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        try
        {
            await client.ConnectAsync(s.ImapHost, s.ImapPort, s.ImapSecurity, c.Ct);
            await client.AuthenticateAsync(c.Cred["email"], c.Cred["password"], c.Ct);
            return client;
        }
        catch (AuthenticationException) { client.Dispose(); throw new AppException(409, $"{c.App.Name} did not accept the sign-in. Use an app password (not your normal password) and reconnect.", "reconnect_required"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or SslHandshakeException or ServiceNotConnectedException or ImapProtocolException or TimeoutException or OperationCanceledException)
        {
            client.Dispose(); throw new AppException(502, $"{c.App.Name} mail could not be reached. {(c.App.Key == "proton-mail" ? "Is Proton Bridge running and reachable from the server?" : "Please try again.")}");
        }
    }

    private static AppItem MailItem(IMessageSummary m) => new(m.UniqueId.Id.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(m.Envelope?.Subject) ? "(no subject)" : m.Envelope!.Subject,
        $"{(m.Envelope?.From.Mailboxes.FirstOrDefault() is { } f ? string.IsNullOrWhiteSpace(f.Name) ? f.Address : $"{f.Name} <{f.Address}>" : "unknown")} · {m.Date.UtcDateTime:d MMM yyyy HH:mm}",
        null, (m.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Seen) ? null : "unread", null, "message");

    private static async Task<IReadOnlyList<AppItem>> ImapListAsync(ProviderCtx c, SearchQuery? query, int limit)
    {
        using var client = await ImapAsync(c);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, c.Ct);
        IList<UniqueId> ids = query is null ? await inbox.SearchAsync(SearchQuery.All, c.Ct) : await inbox.SearchAsync(query, c.Ct);
        var latest = ids.OrderByDescending(i => i.Id).Take(limit).ToList();
        if (latest.Count == 0) { await client.DisconnectAsync(true, c.Ct); return []; }
        var summaries = await inbox.FetchAsync(latest, MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId, c.Ct);
        await client.DisconnectAsync(true, c.Ct);
        return summaries.OrderByDescending(s => s.UniqueId.Id).Select(MailItem).ToList();
    }

    private static async Task<IReadOnlyList<AppItem>> ImapMessageAsync(ProviderCtx c, string arg)
    {
        if (!uint.TryParse(arg, out var uid)) throw new AppException(400, "That message is not valid.");
        using var client = await ImapAsync(c);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, c.Ct);
        MimeMessage message;
        try { message = await client.Inbox.GetMessageAsync(new UniqueId(uid), c.Ct); }
        catch (MessageNotFoundException) { throw new AppException(404, "That message no longer exists."); }
        await client.DisconnectAsync(true, c.Ct);
        var text = message.TextBody ?? Regex.Replace(message.HtmlBody ?? "", "<[^>]+>", " ");
        return [new AppItem(arg, string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject, $"{message.From} · {message.Date.UtcDateTime:d MMM yyyy HH:mm}", null, null, null, null, J.Clip(Regex.Replace(WebUtility.HtmlDecode(text), @"\n{3,}", "\n\n").Trim(), 6000))];
    }

    private static ProviderApp ImapApp(string key, string name, string icon, string color, string blurb, string setup, bool bridge) => new()
    {
        Key = key, Name = name, Icon = icon, Color = color, Account = "Inbox, search and send",
        ConnectTitle = $"Connect {name}", ConnectLabel = $"Connect {name}", ConnectBlurb = blurb,
        WriteLabel = "Allow Daksh to send email", WriteHint = $"Full access lets Daksha send email from your {name} address.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = setup,
        Ask = Rx($"{name.ToLowerInvariant()}|emails?|inbox|unread|mails?"),
        Credentials = new()
        {
            Fields = bridge
                ? [new("email", "Proton address", "text", Placeholder: "you@proton.me"), new("password", "Bridge password", "secret", Hint: "The password shown in Proton Mail Bridge (not your Proton password)."), new("host", "Bridge address", "text", Placeholder: "127.0.0.1", Default: "127.0.0.1"), new("imapPort", "IMAP port", "number", Default: "1143"), new("smtpPort", "SMTP port", "number", Default: "1025")]
                : [new("email", "Yahoo address", "text", Placeholder: "you@yahoo.com"), new("password", "App password", "secret", Hint: "Create one in Yahoo Account Security → Generate app password.")],
            Prepare = async (v, ct) =>
            {
                if (!Regex.IsMatch(v["email"], @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new AppException(400, "Enter your full email address.");
                if (bridge) { await HostGuard.CheckHostAsync(v["host"], ct); foreach (var k in new[] { "imapPort", "smtpPort" }) if (!int.TryParse(v[k], out var p) || p is < 1 or > 65535) throw new AppException(400, "Enter a valid port number."); }
            },
            Probe = async c => { using var client = await ImapAsync(c); await client.DisconnectAsync(true, c.Ct); return new ProfileInfo(c.Cred["email"].ToLowerInvariant(), c.Cred["email"]); },
        },
        Views =
        [
            new ViewSpec { Id = "inbox", Label = "Inbox", Dashboard = true, Load = (c, _) => ImapListAsync(c, null, 25) },
            new ViewSpec { Id = "unread", Label = "Unread", Dashboard = true, Load = (c, _) => ImapListAsync(c, SearchQuery.NotSeen, 25) },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [new("q", "Search mail", Placeholder: "From, subject or text")], Load = (c, i) => ImapListAsync(c, SearchQuery.SubjectContains(i["q"]).Or(SearchQuery.FromContains(i["q"])).Or(SearchQuery.BodyContains(i["q"])), 25) },
            new ViewSpec { Id = "message", Label = "Message", Hidden = true, TakesArg = true, Load = (c, i) => ImapMessageAsync(c, i["arg"]) },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send an email", Inputs = [new("to", "To", Placeholder: "name@example.com"), new("subject", "Subject"), new("body", "Message", "textarea")],
                Run = async (c, i) =>
                {
                    if (!MailboxAddress.TryParse(i["to"], out var to)) throw new AppException(400, "Enter a valid email address to send to.");
                    if (i["subject"].Contains('\n') || i["subject"].Contains('\r')) throw new AppException(400, "The subject must be one line.");
                    var s = MailServerFor(c);
                    var msg = new MimeMessage();
                    msg.From.Add(MailboxAddress.Parse(c.Cred["email"])); msg.To.Add(to); msg.Subject = i["subject"]; msg.Body = new TextPart("plain") { Text = i["body"] };
                    using var smtp = new SmtpClient { Timeout = 20_000 };
                    if (s.TrustLocalCertificate && IsPrivateHost(s.SmtpHost)) smtp.ServerCertificateValidationCallback = (_, _, _, _) => true;
                    try { await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort, s.SmtpSecurity, c.Ct); await smtp.AuthenticateAsync(c.Cred["email"], c.Cred["password"], c.Ct); await smtp.SendAsync(msg, c.Ct); await smtp.DisconnectAsync(true, c.Ct); }
                    catch (AuthenticationException) { throw new AppException(409, $"{c.App.Name} did not accept the sign-in. Reconnect with an app password.", "reconnect_required"); }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or SmtpProtocolException or SmtpCommandException) { throw new AppException(502, $"{c.App.Name} could not send that email."); }
                    return new ActionResult($"Sent to {to.Address}.");
                } },
        ],
    };

    private static ProviderApp YahooMail() => ImapApp("yahoo-mail", "Yahoo Mail", "mono:Y!", "#6001D2",
        "Read and search your Yahoo inbox. Yahoo needs an app password: in Yahoo Account Security choose “Generate app password”, then paste it here with your address. Daksha never sees your normal password.", "Amit.md#yahoo-mail", bridge: false);

    private static ProviderApp ProtonMail() => ImapApp("proton-mail", "Proton Mail", "SiProtonmail", "#6D4AFF",
        "Proton has no public API, so Daksha connects through Proton Mail Bridge (a paid-plan app that runs on your computer). Install Bridge, copy the Bridge password and address, and paste them here. The server running Daksha must be able to reach Bridge.", "Amit.md#proton-mail", bridge: true);

    // ============================================================================================
    // Apple Calendar (iCloud CalDAV)
    // ============================================================================================
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace Cal = "urn:ietf:params:xml:ns:caldav";

    private sealed record CalendarInfo(string Href, string Name);

    private static async Task<XDocument> DavAsync(ProviderCtx c, string method, string url, string body, int depth = 0, IDictionary<string, string>? headers = null)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host == "caldav.icloud.com" || uri.Host.EndsWith("-caldav.icloud.com", StringComparison.Ordinal))) throw new AppException(400, "iCloud sent an address Daksha does not trust.");
        using var req = new HttpRequestMessage(new HttpMethod(method), uri) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Cred["appleId"]}:{c.Cred["password"]}")));
        req.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        if (headers is not null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var res = await c.Clients.CreateClient("provider-apps").SendAsync(req, c.Ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new AppException(409, "Apple did not accept the sign-in. Use an app-specific password and reconnect.", "reconnect_required");
        if (!res.IsSuccessStatusCode) throw new AppException(502, "Apple Calendar could not complete the request.");
        try { return XDocument.Parse(await res.Content.ReadAsStringAsync(c.Ct)); }
        catch (System.Xml.XmlException) { throw new AppException(502, "Apple Calendar sent a reply Daksha could not read."); }
    }

    private static string Absolute(string baseUrl, string href) => new Uri(new Uri(baseUrl), href).ToString();

    private static async Task<string> CalendarHomeAsync(ProviderCtx c)
    {
        var principalDoc = await DavAsync(c, "PROPFIND", "https://caldav.icloud.com/", "<propfind xmlns=\"DAV:\"><prop><current-user-principal/></prop></propfind>");
        var principal = principalDoc.Descendants(D + "current-user-principal").Descendants(D + "href").FirstOrDefault()?.Value ?? throw new AppException(502, "Apple did not return your calendar account.");
        var principalUrl = Absolute("https://caldav.icloud.com/", principal);
        var homeDoc = await DavAsync(c, "PROPFIND", principalUrl, "<propfind xmlns=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><prop><c:calendar-home-set/></prop></propfind>");
        var home = homeDoc.Descendants(Cal + "calendar-home-set").Descendants(D + "href").FirstOrDefault()?.Value ?? throw new AppException(502, "Apple did not return your calendars.");
        return Absolute(principalUrl, home);
    }

    private static async Task<List<CalendarInfo>> CalendarsAsync(ProviderCtx c)
    {
        var home = c.Extra("home");
        if (home.Length == 0) home = await CalendarHomeAsync(c);
        var doc = await DavAsync(c, "PROPFIND", home, "<propfind xmlns=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><prop><displayname/><resourcetype/><c:supported-calendar-component-set/></prop></propfind>", 1);
        var list = new List<CalendarInfo>();
        foreach (var r in doc.Descendants(D + "response"))
        {
            var isCalendar = r.Descendants(D + "resourcetype").Descendants(Cal + "calendar").Any();
            var hasEvents = r.Descendants(Cal + "comp").Any(x => (string?)x.Attribute("name") == "VEVENT");
            var name = r.Descendants(D + "displayname").FirstOrDefault()?.Value;
            var href = r.Element(D + "href")?.Value;
            if (isCalendar && hasEvents && href is not null) list.Add(new CalendarInfo(Absolute(home, href), string.IsNullOrWhiteSpace(name) ? "Calendar" : name));
        }
        return list;
    }

    private static string Unfold(string ics) => Regex.Replace(ics.Replace("\r\n", "\n"), @"\n[ \t]", "");

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    public static (DateTimeOffset? When, bool AllDay) IcsTime(string line)
    {
        // DTSTART;TZID=Asia/Kolkata:20261201T093000   DTSTART:20261201T093000Z   DTSTART;VALUE=DATE:20261201
        var colon = line.IndexOf(':');
        if (colon < 0) return (null, false);
        var head = line[..colon]; var value = line[(colon + 1)..].Trim();
        if (head.Contains("VALUE=DATE", StringComparison.Ordinal) && !head.Contains("DATE-TIME", StringComparison.Ordinal) || value.Length == 8)
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? (new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc)), true) : (null, false);
        if (value.EndsWith('Z')) return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? (new DateTimeOffset(utc, TimeSpan.Zero), false) : (null, false);
        if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return (null, false);
        var tzid = Regex.Match(head, @"TZID=([^;:]+)").Groups[1].Value;
        try { var zone = tzid.Length > 0 ? TimeZoneInfo.FindSystemTimeZoneById(tzid) : TimeZoneInfo.Utc; return (new DateTimeOffset(local, zone.GetUtcOffset(local)), false); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return (new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)), false); }
    }

    public sealed record IcsEvent(string Uid, string Title, DateTimeOffset Start, DateTimeOffset? End, bool AllDay, string? Location, string Calendar);

    public static IEnumerable<IcsEvent> ParseEvents(string ics, string calendar)
    {
        var text = Unfold(ics);
        foreach (Match block in Regex.Matches(text, @"BEGIN:VEVENT\n(.*?)END:VEVENT", RegexOptions.Singleline))
        {
            string? Prop(string name) => Regex.Match(block.Groups[1].Value, $@"^{name}[;:][^\n]*$", RegexOptions.Multiline) is { Success: true } m ? m.Value : null;
            var startLine = Prop("DTSTART"); if (startLine is null) continue;
            var (start, allDay) = IcsTime(startLine); if (start is null) continue;
            var endLine = Prop("DTEND");
            var summary = Prop("SUMMARY"); var location = Prop("LOCATION"); var uid = Prop("UID");
            yield return new IcsEvent(uid is null ? Guid.NewGuid().ToString() : uid[(uid.IndexOf(':') + 1)..].Trim(), summary is null ? "(no title)" : Unescape(summary[(summary.IndexOf(':') + 1)..]), start.Value,
                endLine is null ? null : IcsTime(endLine).When, allDay, location is null ? null : Unescape(location[(location.IndexOf(':') + 1)..]), calendar);
        }
    }

    private static async Task<IReadOnlyList<AppItem>> CalendarEventsAsync(ProviderCtx c, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        var events = new List<IcsEvent>();
        var stamp = "yyyyMMdd'T'HHmmss'Z'";
        var range = $"start=\"{from.UtcDateTime.ToString(stamp, CultureInfo.InvariantCulture)}\" end=\"{to.UtcDateTime.ToString(stamp, CultureInfo.InvariantCulture)}\"";
        foreach (var cal in await CalendarsAsync(c))
        {
            var body = $"<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data><c:expand {range}/></c:calendar-data></d:prop><c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\"><c:time-range {range}/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";
            XDocument doc;
            try { doc = await DavAsync(c, "REPORT", cal.Href, body, 1); }
            catch (AppException ex) when (ex.StatusCode == 502) { continue; }
            foreach (var data in doc.Descendants(Cal + "calendar-data")) events.AddRange(ParseEvents(data.Value, cal.Name));
        }
        return events.Where(e => e.Start < to && (e.End ?? e.Start) >= from).OrderBy(e => e.Start).Take(limit).Select(e => new AppItem($"{e.Uid}-{e.Start.ToUnixTimeSeconds()}", e.Title,
            e.AllDay ? $"{e.Start.UtcDateTime:ddd d MMM} · all day · {e.Calendar}" : $"{e.Start.UtcDateTime:ddd d MMM HH:mm}{(e.End is { } end ? "–" + end.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) : "")} UTC · {e.Calendar}{(string.IsNullOrWhiteSpace(e.Location) ? "" : " · " + e.Location)}",
            null, e.AllDay ? "all day" : null)).ToList();
    }

    private static string IcsEscape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    private static ProviderApp AppleCalendar() => new()
    {
        Key = "apple-calendar", Name = "Apple Calendar", Icon = "SiApple", Color = "#000000", Account = "iCloud calendars and events",
        ConnectTitle = "Connect Apple Calendar", ConnectLabel = "Connect Apple Calendar",
        ConnectBlurb = "Enter your Apple ID and an app-specific password (appleid.apple.com → Sign-In and Security → App-Specific Passwords). Daksha can then show your upcoming events. Daksha never sees your Apple ID password.",
        WriteLabel = "Allow Daksh to add events", WriteHint = "Full access lets Daksha add events to your iCloud calendars.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#apple-calendar",
        Ask = Rx("apple calendar|icloud|calendar|schedule|meetings?|events?|agenda|appointments?"),
        ExtraSettings = [new("timeZone", "My time zone", "text", "Used when Daksha adds an event, for example Asia/Kolkata.", "Asia/Kolkata", null, "Asia/Kolkata")],
        Credentials = new()
        {
            Fields = [new("appleId", "Apple ID", Placeholder: "you@icloud.com"), new("password", "App-specific password", "secret", Placeholder: "xxxx-xxxx-xxxx-xxxx")],
            Probe = async c => { var home = await CalendarHomeAsync(c); return new ProfileInfo(c.Cred["appleId"].ToLowerInvariant(), c.Cred["appleId"], new() { ["home"] = home }); },
        },
        Views =
        [
            new ViewSpec { Id = "upcoming", Label = "Next 30 days", Dashboard = true, Load = (c, _) => CalendarEventsAsync(c, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddDays(30), 60) },
            new ViewSpec { Id = "today", Label = "Today", Dashboard = true, Load = (c, _) => CalendarEventsAsync(c, DateTimeOffset.UtcNow.Date, DateTimeOffset.UtcNow.Date.AddDays(1), 40) },
            new ViewSpec { Id = "calendars", Label = "Calendars", Load = async (c, _) => (await CalendarsAsync(c)).Select(k => new AppItem(k.Href, k.Name)).ToList() },
        ],
        Actions =
        [
            new ActionSpec { Id = "add-event", Label = "Add an event", Inputs = [new("calendar", "Calendar", "select-view", OptionsView: "calendars"), new("title", "Title"), new("start", "Starts", "datetime"), new("end", "Ends", "datetime"), new("location", "Location", Required: false)],
                Run = async (c, i) =>
                {
                    var zone = TimeZoneInfo.Utc;
                    try { if (c.Setting("timeZone").Trim() is { Length: > 0 } tz) zone = TimeZoneInfo.FindSystemTimeZoneById(tz); }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new AppException(400, "Your time zone in Manage is not valid. Use a name like Asia/Kolkata."); }
                    if (!DateTime.TryParse(i["start"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) || !DateTime.TryParse(i["end"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) || e <= s) throw new AppException(400, "Enter a start and a later end time.");
                    var cals = await CalendarsAsync(c);
                    if (!cals.Any(k => k.Href == i["calendar"])) throw new AppException(400, "Choose one of your calendars.");
                    var su = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s, DateTimeKind.Unspecified), zone); var eu = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(e, DateTimeKind.Unspecified), zone);
                    var uid = Guid.NewGuid().ToString();
                    var fmt = "yyyyMMdd'T'HHmmss'Z'";
                    var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Daksha//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:{DateTime.UtcNow.ToString(fmt, CultureInfo.InvariantCulture)}\r\nDTSTART:{su.ToString(fmt, CultureInfo.InvariantCulture)}\r\nDTEND:{eu.ToString(fmt, CultureInfo.InvariantCulture)}\r\nSUMMARY:{IcsEscape(i["title"])}\r\n{(i["location"].Length > 0 ? $"LOCATION:{IcsEscape(i["location"])}\r\n" : "")}END:VEVENT\r\nEND:VCALENDAR\r\n";
                    using var req = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(i["calendar"].EndsWith('/') ? i["calendar"] : i["calendar"] + "/"), uid + ".ics")) { Content = new StringContent(ics, Encoding.UTF8, "text/calendar") };
                    if (req.RequestUri!.Scheme != Uri.UriSchemeHttps || !req.RequestUri.Host.EndsWith("icloud.com", StringComparison.Ordinal)) throw new AppException(400, "That calendar address is not allowed.");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Cred["appleId"]}:{c.Cred["password"]}")));
                    req.Headers.TryAddWithoutValidation("If-None-Match", "*");
                    using var res = await c.Clients.CreateClient("provider-apps").SendAsync(req, c.Ct);
                    if (!res.IsSuccessStatusCode) throw new AppException(502, "Apple Calendar did not accept the event.");
                    return new ActionResult("Event added.");
                } },
        ],
    };
}
