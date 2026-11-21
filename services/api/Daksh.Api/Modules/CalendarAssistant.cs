using Daksh.Api.Auth;
using System.Text.Json;
using Daksh.Platform.Security;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record CalendarEventDto(string Id, string Title, string Start, string End, bool AllDay, string? Location, string? HtmlLink, string AccountId = "", string Account = "", string? ICalUid = null);
/// <summary>One connected Google account and whether it can be used for Calendar (<see cref="Error"/> is set when reading it failed this time).</summary>
public sealed record CalendarAccountInfo(string Id, string Email, bool HasCalendar, bool IsDefault, string? Error = null);
public sealed record CalendarListing(IReadOnlyList<CalendarEventDto> Events, IReadOnlyList<CalendarAccountInfo> Accounts);
public enum CalendarStatus { Ok, NeedsAccess, Failed }
public sealed record CalendarResult<T>(CalendarStatus Status, T? Value = default, string? Message = null);

/// <summary>
/// Google Calendar for the owner, through the same Google sign-in as Gmail (the Calendar scope is requested when a Gmail account is connected).
/// Shared by the Calendar screens (web and mobile) and by the owner's own chat ("add dentist tomorrow 5pm", "what's on my calendar today").
/// </summary>
public static class CalendarAssistant
{
    private const string Api = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
    public const string ReconnectMessage = "ℹ️ I don't have Google Calendar access yet. Disconnect and reconnect your Gmail account in Daksha (tick the Calendar permission when Google asks), then ask again.";

    /// <summary>All the owner's enabled Google accounts, default first, each marked with whether it was granted Calendar.</summary>
    public static async Task<IReadOnlyList<CalendarAccountInfo>> AccountsAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var accounts = await db.GetCollection<GmailAccountDoc>("gmailAccounts").Find(a => a.UserId == userId && a.Enabled).ToListAsync(ct);
        var list = accounts.OrderByDescending(a => a.IsDefault).ThenBy(a => a.Email)
            .Select(a => new CalendarAccountInfo(a.ConnectionId, a.Email, !string.IsNullOrWhiteSpace(a.GrantedScopes) && GmailScopes.Satisfies(GmailScopes.Parse(a.GrantedScopes), GmailScopes.Calendar), a.IsDefault))
            .ToList();
        // The owner's Outlook calendar, when Microsoft is connected with Calendar, is one more account (listed after the Google ones).
        if (await MicrosoftCalendar.InfoAsync(db, userId, ct) is { } outlook) list.Add(outlook with { IsDefault = list.Count == 0 });
        return list;
    }

    /// <summary>The connection to use for a write: the requested account if it has Calendar, otherwise the default one that does. Null when none can.</summary>
    public static async Task<string?> CalendarAccountAsync(IMongoDatabase db, string userId, string? preferred, CancellationToken ct)
    {
        // A read-only Outlook account can be read but not written to, so it is never picked for a write.
        var all = (await AccountsAsync(db, userId, ct)).Where(a => a.HasCalendar).ToList();
        var usable = new List<CalendarAccountInfo>();
        foreach (var a in all)
            if (!await MicrosoftCalendar.OwnsAsync(db, userId, a.Id, ct) || await MicrosoftCalendar.CanWriteAsync(db, userId, a.Id, ct)) usable.Add(a);
        return (usable.FirstOrDefault(a => a.Id == preferred) ?? usable.FirstOrDefault())?.Id;
    }

    public static async Task<TimeZoneInfo> TimezoneAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var user = await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        return BusyMode.ResolveTimezone(user?.Busy ?? new BusyProfile());
    }

    private static CalendarEventDto ToDto(JsonElement e, TimeZoneInfo tz, CalendarAccountInfo? account = null)
    {
        string Stamp(JsonElement part, out bool allDay)
        {
            allDay = false;
            if (part.TryGetProperty("dateTime", out var dt) && DateTimeOffset.TryParse(dt.GetString(), out var when)) return TimeZoneInfo.ConvertTime(when, tz).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            allDay = true;
            return part.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        }
        var start = Stamp(e.GetProperty("start"), out var allDay);
        var end = e.TryGetProperty("end", out var ep) ? Stamp(ep, out _) : start;
        string? Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new CalendarEventDto(Str("id") ?? "", Str("summary") ?? "(no title)", start, end, allDay, Str("location"), Str("htmlLink"), account?.Id ?? "", account?.Email ?? "", Str("iCalUID"));
    }

    /// <summary>The events of ALL the owner's Google accounts that have Calendar, merged by time, from the start of <paramref name="fromLocal"/> to the start of <paramref name="toLocal"/> (local wall-clock times).
    /// One account failing never hides the others: its <see cref="CalendarAccountInfo.Error"/> says why.</summary>
    public static async Task<CalendarResult<CalendarListing>> ListAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients,
        string userId, DateTime fromLocal, DateTime toLocal, CancellationToken ct)
    {
        var accounts = await AccountsAsync(db, userId, ct);
        var usable = accounts.Where(a => a.HasCalendar).ToList();
        if (usable.Count == 0) return new(CalendarStatus.NeedsAccess, new CalendarListing([], accounts), ReconnectMessage);
        var tz = await TimezoneAsync(db, userId, ct);
        string Utc(DateTime local) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz), TimeSpan.Zero).ToString("O");
        var url = Api + $"?singleEvents=true&orderBy=startTime&maxResults=250&timeMin={Uri.EscapeDataString(Utc(fromLocal))}&timeMax={Uri.EscapeDataString(Utc(toLocal))}";

        var perAccount = await Task.WhenAll(usable.Select(async a =>
        {
            try
            {
                if (await MicrosoftCalendar.OwnsAsync(db, userId, a.Id, ct))
                    return (Account: a, Events: (await MicrosoftCalendar.ListAsync(db, protector, config, clients, userId, fromLocal, toLocal, ct)).ToList(), Error: (string?)null);
                using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, a.Id, url, HttpMethod.Get, null, db, protector, config, clients, ct);
                var events = doc.RootElement.TryGetProperty("items", out var items)
                    ? items.EnumerateArray().Where(e => !(e.TryGetProperty("status", out var st) && st.GetString() == "cancelled")).Select(e => ToDto(e, tz, a)).ToList()
                    : [];
                return (Account: a, Events: events, Error: (string?)null);
            }
            catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return (Account: a, Events: new List<CalendarEventDto>(), Error: (string?)"Reconnect this account and tick Calendar."); }
            catch (GmailIntegrationException ex) { return (Account: a, Events: new List<CalendarEventDto>(), Error: (string?)ex.Message); }
            catch (MicrosoftException ex) { return (Account: a, Events: new List<CalendarEventDto>(), Error: (string?)ex.Message); }
            catch (HttpRequestException) { return (Account: a, Events: new List<CalendarEventDto>(), Error: (string?)"Microsoft could not be reached."); }
        }));

        // The same meeting on two of your accounts (an invite sent to both) is shown once.
        var seen = new HashSet<string>();
        var merged = perAccount.SelectMany(r => r.Events)
            .OrderBy(e => e.Start, StringComparer.Ordinal)
            .Where(e => e.ICalUid is null || seen.Add(e.ICalUid + "|" + e.Start))
            .ToList();
        var info = accounts.Select(a => perAccount.FirstOrDefault(r => r.Account.Id == a.Id) is { Error: { } err } ? a with { Error = err } : a).ToList();
        var allFailed = perAccount.All(r => r.Error is not null);
        return new(allFailed ? CalendarStatus.Failed : CalendarStatus.Ok, new CalendarListing(merged, info), allFailed ? perAccount[0].Error : null);
    }

    /// <summary>Adds an event. <paramref name="startLocal"/> is the owner's local wall-clock time.</summary>
    public static async Task<CalendarResult<CalendarEventDto>> CreateEventAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId,
        string title, DateTime startLocal, int minutes, string? location, CancellationToken ct, string? accountId = null)
    {
        var connection = await CalendarAccountAsync(db, userId, accountId, ct);
        if (connection is null) return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage);
        var tz = await TimezoneAsync(db, userId, ct);
        var end = startLocal.AddMinutes(Math.Clamp(minutes, 5, 24 * 60));
        object Stamp(DateTime t) => new { dateTime = t.ToString("yyyy-MM-dd'T'HH:mm:ss"), timeZone = tz.Id };
        try
        {
            if (await MicrosoftCalendar.OwnsAsync(db, userId, connection, ct))
                return new(CalendarStatus.Ok, await MicrosoftCalendar.CreateAsync(db, protector, config, clients, userId, title, startLocal, minutes, location, ct));
            using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, connection, Api, HttpMethod.Post,
                new { summary = title, location, start = Stamp(startLocal), end = Stamp(end) }, db, protector, config, clients, ct);
            var used = (await AccountsAsync(db, userId, ct)).FirstOrDefault(a => a.Id == connection);
            return new(CalendarStatus.Ok, ToDto(doc.RootElement, tz, used));
        }
        catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage); }
        catch (GmailIntegrationException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
        catch (MicrosoftException ex) when (ex.StatusCode is 401 or 403 or 409) { return new(CalendarStatus.NeedsAccess, Message: ex.Message); }
        catch (MicrosoftException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
    }

    /// <summary>Changes an event on the account it lives on. Null fields are left alone; <paramref name="location"/> "" clears it. Without <paramref name="startLocal"/> the times are not touched (all-day events).</summary>
    public static async Task<CalendarResult<CalendarEventDto>> UpdateEventAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string eventId, string? accountId,
        string? title, DateTime? startLocal, int? minutes, string? location, CancellationToken ct)
    {
        // Unlike a create, an edit must go to the account the event is on: falling back to another account would 404 or hit a different event.
        var account = (await AccountsAsync(db, userId, ct)).FirstOrDefault(a => a.Id == accountId && a.HasCalendar);
        if (account is null) return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage);
        var tz = await TimezoneAsync(db, userId, ct);
        if (await MicrosoftCalendar.OwnsAsync(db, userId, account.Id, ct))
        {
            try { return new(CalendarStatus.Ok, await MicrosoftCalendar.UpdateAsync(db, protector, config, clients, userId, eventId, title, startLocal, minutes, location, ct)); }
            catch (MicrosoftException ex) when (ex.StatusCode is 401 or 403 or 409) { return new(CalendarStatus.NeedsAccess, Message: ex.Message); }
            catch (MicrosoftException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
        }
        var body = new Dictionary<string, object?>();
        if (title is not null) body["summary"] = title;
        if (location is not null) body["location"] = location.Length == 0 ? null : location;
        if (startLocal is { } start)
        {
            object Stamp(DateTime t) => new { dateTime = t.ToString("yyyy-MM-dd'T'HH:mm:ss"), timeZone = tz.Id };
            body["start"] = Stamp(start);
            body["end"] = Stamp(start.AddMinutes(Math.Clamp(minutes ?? 60, 5, 24 * 60)));
        }
        try
        {
            using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, account.Id, $"{Api}/{Uri.EscapeDataString(eventId)}", HttpMethod.Patch, body, db, protector, config, clients, ct);
            return new(CalendarStatus.Ok, ToDto(doc.RootElement, tz, account));
        }
        catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage); }
        catch (GmailIntegrationException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
    }

    public static async Task<CalendarResult<bool>> DeleteEventAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string eventId, string? accountId, CancellationToken ct)
    {
        var connection = await CalendarAccountAsync(db, userId, accountId, ct);
        if (connection is null) return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage);
        if (await MicrosoftCalendar.OwnsAsync(db, userId, connection, ct))
        {
            try { await MicrosoftCalendar.DeleteAsync(db, protector, config, clients, userId, eventId, ct); return new(CalendarStatus.Ok, true); }
            catch (MicrosoftException ex) when (ex.StatusCode is 401 or 403 or 409) { return new(CalendarStatus.NeedsAccess, Message: ex.Message); }
            catch (MicrosoftException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
        }
        try
        {
            using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, connection, $"{Api}/{Uri.EscapeDataString(eventId)}", HttpMethod.Delete, null, db, protector, config, clients, ct);
            return new(CalendarStatus.Ok, true);
        }
        catch (GmailIntegrationException ex) when (ex.StatusCode == 404 || ex.StatusCode == 410) { return new(CalendarStatus.Ok, true); }
        catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return new(CalendarStatus.NeedsAccess, Message: ReconnectMessage); }
        catch (GmailIntegrationException ex) { return new(CalendarStatus.Failed, Message: ex.Message); }
    }

    // ---- Chat wording (own-chat commands) ------------------------------------------------------------

    public static async Task<string> CreateAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId,
        string title, DateTime startLocal, int minutes, string? location, CancellationToken ct)
    {
        var r = await CreateEventAsync(db, protector, config, clients, userId, title, startLocal, minutes, location, ct);
        return r.Status switch
        {
            CalendarStatus.Ok => $"✅ Added \"{title}\" to your Google Calendar on {startLocal:ddd d MMM, h:mm tt}.",
            CalendarStatus.NeedsAccess => ReconnectMessage,
            _ => "ℹ️ I couldn't add that to your calendar: " + r.Message,
        };
    }

    public static async Task<string> AgendaAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId,
        DateTime fromLocal, DateTime toLocal, CancellationToken ct)
    {
        var r = await ListAsync(db, protector, config, clients, userId, fromLocal, toLocal, ct);
        if (r.Status == CalendarStatus.NeedsAccess) return ReconnectMessage;
        if (r.Status == CalendarStatus.Failed) return "ℹ️ I couldn't read your calendar: " + r.Message;
        if (r.Value is not { Events.Count: > 0 } listing) return "📅 Nothing on your calendar in that time.";
        var many = listing.Accounts.Count(a => a.HasCalendar) > 1;
        string Who(CalendarEventDto e) => many ? $" ({e.Account})" : "";
        var lines = listing.Events.Take(25).Select(e => e.AllDay ? $"• {e.Start} (all day) - {e.Title}{Who(e)}" : $"• {DateTimeOffset.Parse(e.Start):ddd d MMM, h:mm tt} - {e.Title}{Who(e)}");
        return "📅 Your calendar:\n" + string.Join("\n", lines);
    }
}

public sealed record UpdateCalendarEventRequest(string? Title, string? Start, int? Minutes, string? Location, string? AccountId);
public sealed record CreateCalendarEventRequest(string Title, string Start, int? Minutes, string? Location, string? AccountId = null);

public static class CalendarModule
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/calendar").WithTags("Calendar").RequireAuthorization(AuthSetup.UserScheme);

        // from/to are local dates (yyyy-MM-dd); "to" is inclusive. Defaults to the next 7 days.
        g.MapGet("/events", async (string? from, string? to, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var tz = await CalendarAssistant.TimezoneAsync(db, who.UserId, ct);
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
            var f = DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fd) ? fd.Date : today;
            var t = DateTime.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var td) ? td.Date : f.AddDays(6);
            if (t < f || t - f > TimeSpan.FromDays(62)) return Results.Problem(statusCode: 400, title: "Choose a date range of up to two months.");
            var r = await CalendarAssistant.ListAsync(db, protector, config, clients, who.UserId, f, t.AddDays(1), ct);
            return Results.Ok(new { connected = r.Status != CalendarStatus.NeedsAccess, status = r.Status.ToString(), message = r.Status == CalendarStatus.Ok ? null : r.Message, timezone = tz.Id, today = today.ToString("yyyy-MM-dd"), events = r.Value?.Events ?? [], accounts = r.Value?.Accounts ?? [] });
        });

        g.MapPost("/events", async (CreateCalendarEventRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200) return Results.Problem(statusCode: 400, title: "Give the event a title (up to 200 characters).");
            if (!DateTime.TryParse(req.Start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)) return Results.Problem(statusCode: 400, title: "Choose a start date and time.");
            var r = await CalendarAssistant.CreateEventAsync(db, protector, config, clients, who.UserId, req.Title.Trim(), start, req.Minutes ?? 60, string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim(), ct, req.AccountId);
            return r.Status switch
            {
                CalendarStatus.Ok => Results.Ok(r.Value),
                CalendarStatus.NeedsAccess => Results.Problem(statusCode: 409, title: "Google Calendar isn't connected.", detail: "Reconnect your Gmail account and tick the Calendar permission."),
                _ => Results.Problem(statusCode: 502, title: "Google Calendar could not add the event.", detail: r.Message),
            };
        });

        g.MapPatch("/events/{id}", async (string id, UpdateCalendarEventRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (req.Title is not null && (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200)) return Results.Problem(statusCode: 400, title: "Give the event a title (up to 200 characters).");
            DateTime? start = null;
            if (req.Start is not null)
            {
                if (!DateTime.TryParse(req.Start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var s)) return Results.Problem(statusCode: 400, title: "Choose a valid start date and time.");
                start = s;
            }
            var r = await CalendarAssistant.UpdateEventAsync(db, protector, config, clients, who.UserId, id, req.AccountId, req.Title?.Trim(), start, req.Minutes, req.Location?.Trim(), ct);
            return r.Status switch
            {
                CalendarStatus.Ok => Results.Ok(r.Value),
                CalendarStatus.NeedsAccess => Results.Problem(statusCode: 409, title: "Google Calendar isn't connected for that account."),
                _ => Results.Problem(statusCode: 502, title: "Google Calendar could not save the change.", detail: r.Message),
            };
        });

        g.MapDelete("/events/{id}", async (string id, string? account, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var r = await CalendarAssistant.DeleteEventAsync(db, protector, config, clients, who.UserId, id, account, ct);
            return r.Status switch
            {
                CalendarStatus.Ok => Results.NoContent(),
                CalendarStatus.NeedsAccess => Results.Problem(statusCode: 409, title: "Google Calendar isn't connected."),
                _ => Results.Problem(statusCode: 502, title: "Google Calendar could not delete the event.", detail: r.Message),
            };
        });
    }
}
