using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Platform.Security;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>Outlook Calendar for the owner, through the same Microsoft sign-in. Used by the Calendar screens and by the chat assistants alongside Google Calendar.</summary>
public static class MicrosoftCalendar
{
    /// <summary>Graph wants Windows time-zone names for some calls; fall back to the id we have when there is no mapping.</summary>
    public static string GraphZone(TimeZoneInfo tz) =>
        tz.Id.Contains('/') && TimeZoneInfo.TryConvertIanaIdToWindowsId(tz.Id, out var win) && !string.IsNullOrEmpty(win) ? win : tz.Id;

    public static async Task<MicrosoftAccountDoc?> CalendarAccountAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var a = await MicrosoftIntegrationModule.LoadAccountAsync(db, userId, ct);
        return a is not null && a.Features.Contains(MicrosoftFeatures.Calendar) ? a : null;
    }

    /// <summary>The account as a calendar account for the Calendar screen's account list.</summary>
    public static async Task<CalendarAccountInfo?> InfoAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var a = await CalendarAccountAsync(db, userId, ct);
        return a is null ? null : new CalendarAccountInfo(a.ConnectionId, a.Email, true, false);
    }

    public static async Task<bool> CanWriteAsync(IMongoDatabase db, string userId, string? accountId, CancellationToken ct) =>
        await CalendarAccountAsync(db, userId, ct) is { } a && a.ConnectionId == accountId && a.Write;

    private static MicrosoftException ReadOnly() => new(403, "Daksha has read-only access to your Outlook calendar. Turn on full access in Manage to change events.", "write_required");

    public static async Task<bool> OwnsAsync(IMongoDatabase db, string userId, string? accountId, CancellationToken ct) =>
        !string.IsNullOrEmpty(accountId) && await CalendarAccountAsync(db, userId, ct) is { } a && a.ConnectionId == accountId;

    private static CalendarEventDto ToDto(JsonElement e, TimeZoneInfo tz, MicrosoftAccountDoc account)
    {
        string? Str(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var allDay = e.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True;
        string Stamp(JsonElement part)
        {
            var raw = Str(part, "dateTime") ?? "";
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return raw;
            if (allDay) return local.ToString("yyyy-MM-dd");
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        }
        var start = e.TryGetProperty("start", out var sp) ? Stamp(sp) : "";
        var end = e.TryGetProperty("end", out var ep) ? Stamp(ep) : start;
        var location = e.TryGetProperty("location", out var lp) ? Str(lp, "displayName") : null;
        return new CalendarEventDto(Str(e, "id") ?? "", Str(e, "subject") ?? "(no title)", start, end, allDay, string.IsNullOrWhiteSpace(location) ? null : location, Str(e, "webLink"), account.ConnectionId, account.Email, Str(e, "iCalUId"));
    }

    /// <summary>Events from the start of <paramref name="fromLocal"/> to the start of <paramref name="toLocal"/> (local wall-clock times). Empty when Outlook Calendar is not enabled.</summary>
    public static async Task<IReadOnlyList<CalendarEventDto>> ListAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, DateTime fromLocal, DateTime toLocal, CancellationToken ct)
    {
        var account = await CalendarAccountAsync(db, userId, ct);
        if (account is null) return [];
        var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
        string Utc(DateTime local) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz), TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var path = $"me/calendarView?startDateTime={Uri.EscapeDataString(Utc(fromLocal))}&endDateTime={Uri.EscapeDataString(Utc(toLocal))}&$top=250&$orderby=start/dateTime&$select=id,subject,start,end,isAllDay,location,webLink,iCalUId,isCancelled";
        using var doc = await MicrosoftIntegrationModule.GraphAsync(userId, account, path, HttpMethod.Get, null, db, protector, config, clients, ct, $"outlook.timezone=\"{GraphZone(tz)}\"");
        return doc.RootElement.TryGetProperty("value", out var v)
            ? v.EnumerateArray().Where(e => !(e.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True)).Select(e => ToDto(e, tz, account)).ToList()
            : [];
    }

    public static async Task<CalendarEventDto> CreateAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string title, DateTime startLocal, int minutes, string? location, CancellationToken ct)
    {
        var account = await CalendarAccountAsync(db, userId, ct) ?? throw new MicrosoftException(409, "Outlook Calendar is not enabled.", "not_connected");
        if (!account.Write) throw ReadOnly();
        var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
        var zone = GraphZone(tz);
        var end = startLocal.AddMinutes(Math.Clamp(minutes, 5, 24 * 60));
        object Stamp(DateTime t) => new { dateTime = t.ToString("yyyy-MM-dd'T'HH:mm:ss"), timeZone = zone };
        using var doc = await MicrosoftIntegrationModule.GraphAsync(userId, account, "me/events", HttpMethod.Post,
            new { subject = title, location = location is null ? null : new { displayName = location }, start = Stamp(startLocal), end = Stamp(end) }, db, protector, config, clients, ct, $"outlook.timezone=\"{zone}\"");
        return ToDto(doc.RootElement, tz, account);
    }

    public static async Task<CalendarEventDto> UpdateAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string eventId, string? title, DateTime? startLocal, int? minutes, string? location, CancellationToken ct)
    {
        var account = await CalendarAccountAsync(db, userId, ct) ?? throw new MicrosoftException(409, "Outlook Calendar is not enabled.", "not_connected");
        if (!account.Write) throw ReadOnly();
        var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
        var zone = GraphZone(tz);
        var body = new Dictionary<string, object?>();
        if (title is not null) body["subject"] = title;
        if (location is not null) body["location"] = new { displayName = location };
        if (startLocal is { } s)
        {
            object Stamp(DateTime t) => new { dateTime = t.ToString("yyyy-MM-dd'T'HH:mm:ss"), timeZone = zone };
            body["start"] = Stamp(s);
            body["end"] = Stamp(s.AddMinutes(Math.Clamp(minutes ?? 60, 5, 24 * 60)));
        }
        using var doc = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/events/{Uri.EscapeDataString(eventId)}", HttpMethod.Patch, body, db, protector, config, clients, ct, $"outlook.timezone=\"{zone}\"");
        return ToDto(doc.RootElement, tz, account);
    }

    public static async Task DeleteAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string eventId, CancellationToken ct)
    {
        var account = await CalendarAccountAsync(db, userId, ct) ?? throw new MicrosoftException(409, "Outlook Calendar is not enabled.", "not_connected");
        if (!account.Write) throw ReadOnly();
        try { using var _ = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/events/{Uri.EscapeDataString(eventId)}", HttpMethod.Delete, null, db, protector, config, clients, ct); }
        catch (MicrosoftException ex) when (ex.StatusCode is 404 or 410) { /* already gone */ }
    }
}

/// <summary>What the chat assistants (web chat, and the owner's own WhatsApp / Telegram chat) can see of the owner's Microsoft account when answering a question.</summary>
public static class MicrosoftAssistant
{
    private static readonly Regex MailRx = new(@"\b(mails?|e-?mails?|outlook|inbox|unread|subject|invoices?|newsletters?|sent me|wrote|message from|replied)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CalendarRx = new(@"\b(calendar|agenda|schedule|meetings?|appointments?|events?|free|busy|tomorrow|today|this week|next week|standup|call)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilesRx = new(@"\b(files?|documents?|docs?|onedrive|drive|folders?|spreadsheets?|excel|word|powerpoint|pdf|presentation|slides?|attachment)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DevOpsRx = new(@"\b(devops|azure|work ?items?|tickets?|bugs?|sprints?|backlog|boards?|assigned|stories|story|pipelines?|repos?|pull requests?|prs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OverviewRx = new(@"\b(summary|summari[sz]e|catch me up|brief(ing)?|what.?s new|overview|status|what do i have|my day|anything)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string StripHtml(string html)
    {
        var noBlocks = Regex.Replace(html, @"<(style|script)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(noBlocks, @"<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>Plain text about the owner's Outlook mail, calendar, OneDrive and Azure DevOps that is relevant to <paramref name="question"/>, plus the names of the sources used. Empty when Microsoft is not connected or nothing is relevant.</summary>
    public static async Task<AssistantContextResult> BuildAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string question, CancellationToken ct)
    {
        var account = await MicrosoftIntegrationModule.LoadAccountAsync(db, userId, ct);
        if (account is null || account.Features.Count == 0) return new AssistantContextResult("", []);
        var sb = new StringBuilder();
        var sources = new List<string>();
        var overview = OverviewRx.IsMatch(question);
        var anyNamed = MailRx.IsMatch(question) || CalendarRx.IsMatch(question) || FilesRx.IsMatch(question) || DevOpsRx.IsMatch(question);
        // A real question with no app named (or an overview request) gets a light look at everything the owner enabled.
        var broad = overview || (!anyNamed && AssistantIntent.IsSubstantive(question));
        var words = AssistantIntent.Keywords(question).Where(w => !AssistantIntent.MailWords.Contains(w)).ToList();

        async Task Section(string feature, string label, bool wanted, Func<Task> body)
        {
            if (!wanted || !account.Features.Contains(feature)) return;
            try { await body(); sources.Add(label); }
            catch (MicrosoftException ex) { sb.AppendLine().AppendLine($"{label} is connected but could not be read right now: {ex.Message}"); }
            catch (HttpRequestException) { sb.AppendLine().AppendLine($"{label} could not be reached right now."); }
        }

        await Section(MicrosoftFeatures.Mail, "Outlook mail", MailRx.IsMatch(question) || broad, async () =>
        {
            const string select = "id,subject,from,receivedDateTime,bodyPreview,isRead";
            using var recent = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/mailFolders/inbox/messages?$top=10&$select={select}&$orderby=receivedDateTime desc", HttpMethod.Get, null, db, protector, config, clients, ct);
            var rows = recent.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().Select(MicrosoftIntegrationModule.ToSummary).ToList() : [];
            sb.AppendLine().AppendLine($"Recent Outlook emails in {account.Email} (newest first):");
            foreach (var m in rows) sb.AppendLine($"- {(m.IsRead ? "" : "[unread] ")}{m.Received?.ToLocalTime():d MMM HH:mm} From {AssistantContextBuilder.Clip(m.From, 60)} — {AssistantContextBuilder.Clip(m.Subject, 90)}: {AssistantContextBuilder.Clip(m.Preview, 140)}");
            if (rows.Count == 0) sb.AppendLine("(the inbox is empty)");

            var hitIds = new List<string>();
            if (words.Count > 0)
            {
                var q = string.Join(" ", words.Take(4)).Replace("\"", "");
                using var found = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/messages?$top=6&$select={select}&$search=\"{Uri.EscapeDataString(q)}\"", HttpMethod.Get, null, db, protector, config, clients, ct);
                var hits = found.RootElement.TryGetProperty("value", out var fv) ? fv.EnumerateArray().Select(MicrosoftIntegrationModule.ToSummary).ToList() : [];
                if (hits.Count > 0)
                {
                    sb.AppendLine().AppendLine($"Outlook emails that mention {string.Join(", ", words.Take(4).Select(w => $"\"{w}\""))}:");
                    foreach (var m in hits) sb.AppendLine($"- {m.Received?.ToLocalTime():d MMM yyyy HH:mm} From {AssistantContextBuilder.Clip(m.From, 60)} — {AssistantContextBuilder.Clip(m.Subject, 90)}: {AssistantContextBuilder.Clip(m.Preview, 140)}");
                    hitIds.AddRange(hits.Take(2).Select(m => m.Id));
                }
            }
            // Read the actual emails, not only their previews: what matched the question, else the newest unread.
            if (hitIds.Count == 0) hitIds.AddRange(rows.Where(m => !m.IsRead).Take(2).Select(m => m.Id));
            foreach (var id in hitIds)
            {
                using var full = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/messages/{Uri.EscapeDataString(id)}?$select=subject,from,receivedDateTime,body", HttpMethod.Get, null, db, protector, config, clients, ct, "outlook.body-content-type=\"text\"");
                var root = full.RootElement;
                var body = root.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (b.ValueKind == JsonValueKind.Object && string.Equals(b.TryGetProperty("contentType", out var ct2) ? ct2.GetString() : "", "html", StringComparison.OrdinalIgnoreCase)) body = StripHtml(body);
                var summary = MicrosoftIntegrationModule.ToSummary(root);
                if (body.Length > 0) sb.AppendLine().AppendLine($"Full text of the email \"{AssistantContextBuilder.Clip(summary.Subject, 80)}\" from {AssistantContextBuilder.Clip(summary.From, 50)} ({summary.Received?.ToLocalTime():d MMM HH:mm}):").AppendLine(AssistantContextBuilder.Clip(body, 1500));
            }
        });

        await Section(MicrosoftFeatures.Calendar, "Outlook calendar", CalendarRx.IsMatch(question) || broad, async () =>
        {
            var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
            var events = await MicrosoftCalendar.ListAsync(db, protector, config, clients, userId, today, today.AddDays(8), ct);
            sb.AppendLine().AppendLine($"Outlook calendar for the next 7 days (today is {today:ddd d MMM}):");
            foreach (var e in events.Take(25))
                sb.AppendLine(e.AllDay ? $"- {e.Start} (all day) {e.Title}" : $"- {(DateTimeOffset.TryParse(e.Start, out var s) ? s.ToString("ddd d MMM, h:mm tt") : e.Start)} {e.Title}{(e.Location is null ? "" : $" @ {e.Location}")}");
            if (events.Count == 0) sb.AppendLine("(nothing scheduled)");
        });

        await Section(MicrosoftFeatures.OneDrive, "OneDrive", FilesRx.IsMatch(question), async () =>
        {
            if (words.Count > 0)
            {
                var q = string.Join(" ", words.Take(3)).Replace("'", "''");
                using var found = await MicrosoftIntegrationModule.GraphAsync(userId, account, $"me/drive/root/search(q='{Uri.EscapeDataString(q)}')?$top=10&$select=name,size,lastModifiedDateTime,webUrl,folder,file", HttpMethod.Get, null, db, protector, config, clients, ct);
                var items = found.RootElement.TryGetProperty("value", out var v) ? v.EnumerateArray().ToList() : [];
                sb.AppendLine().AppendLine($"OneDrive files matching {string.Join(", ", words.Take(3).Select(w => $"\"{w}\""))}:");
                foreach (var i in items) sb.AppendLine($"- {(i.TryGetProperty("folder", out _) ? "[folder] " : "")}{(i.TryGetProperty("name", out var n) ? n.GetString() : "")} (changed {(i.TryGetProperty("lastModifiedDateTime", out var d) && DateTimeOffset.TryParse(d.GetString(), out var dt) ? dt.ToLocalTime().ToString("d MMM yyyy") : "?")})");
                if (items.Count == 0) sb.AppendLine("(no matching files)");
            }
            using var recent = await MicrosoftIntegrationModule.GraphAsync(userId, account, "me/drive/recent?$top=8", HttpMethod.Get, null, db, protector, config, clients, ct);
            sb.AppendLine().AppendLine("Recently used OneDrive files:");
            if (recent.RootElement.TryGetProperty("value", out var rv)) foreach (var i in rv.EnumerateArray()) sb.AppendLine($"- {(i.TryGetProperty("name", out var n) ? n.GetString() : "")}");
        });

        await Section(MicrosoftFeatures.DevOps, "Azure DevOps", DevOpsRx.IsMatch(question) || overview, async () =>
        {
            var r = await MicrosoftIntegrationModule.DevOpsWorkItemsAsync(new MicrosoftIntegrationModule.Ctx(account, db, protector, config, clients, userId), null, ct);
            sb.AppendLine().AppendLine($"Azure DevOps (organization {r.Org ?? "none found"}; projects: {string.Join(", ", r.Projects.Take(15))}):");
            sb.AppendLine("Work items assigned to the owner that are still open:");
            foreach (var w in r.Items) sb.AppendLine($"- #{w.Id} [{w.Type}] {AssistantContextBuilder.Clip(w.Title, 100)} — {w.State} ({w.Project})");
            if (r.Items.Count == 0) sb.AppendLine("(none)");
        });

        return new AssistantContextResult(sb.ToString().TrimEnd(), sources);
    }
}
