using System.Text.Json;
using Daksh.Platform.Security;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>
/// Google Calendar for the owner, through the same Google sign-in as Gmail (the Calendar scope is requested when a Gmail account is connected).
/// Used from the owner's own chat: "add dentist tomorrow 5pm" / "what's on my calendar today".
/// </summary>
public static class CalendarAssistant
{
    private const string Api = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
    public const string ReconnectMessage = "ℹ️ I don't have Google Calendar access yet. Disconnect and reconnect your Gmail account in Daksha (tick the Calendar permission when Google asks), then ask again.";

    /// <summary>The connection of the owner's first enabled Google account that was granted Calendar (default account first), or null.</summary>
    private static async Task<string?> CalendarAccountAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var accounts = await db.GetCollection<GmailAccountDoc>("gmailAccounts").Find(a => a.UserId == userId && a.Enabled).ToListAsync(ct);
        return accounts.OrderByDescending(a => a.IsDefault)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.GrantedScopes) && GmailScopes.Satisfies(GmailScopes.Parse(a.GrantedScopes), GmailScopes.Calendar))?.ConnectionId;
    }

    public static async Task<TimeZoneInfo> TimezoneAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var user = await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        return BusyMode.ResolveTimezone(user?.Busy ?? new BusyProfile());
    }

    /// <summary>Adds an event. <paramref name="startLocal"/> is the owner's local wall-clock time. Returns the message to send back.</summary>
    public static async Task<string> CreateAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId,
        string title, DateTime startLocal, int minutes, string? location, CancellationToken ct)
    {
        var connection = await CalendarAccountAsync(db, userId, ct);
        if (connection is null) return ReconnectMessage;
        var tz = await TimezoneAsync(db, userId, ct);
        var end = startLocal.AddMinutes(Math.Clamp(minutes, 5, 24 * 60));
        object Stamp(DateTime t) => new { dateTime = t.ToString("yyyy-MM-dd'T'HH:mm:ss"), timeZone = tz.Id };
        try
        {
            using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, connection, Api, HttpMethod.Post,
                new { summary = title, location, start = Stamp(startLocal), end = Stamp(end) }, db, protector, config, clients, ct);
            return $"✅ Added \"{title}\" to your Google Calendar on {startLocal:ddd d MMM, h:mm tt}.";
        }
        catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return ReconnectMessage; }
        catch (GmailIntegrationException ex) { return "ℹ️ I couldn't add that to your calendar: " + ex.Message; }
    }

    /// <summary>The owner's events between two local times, formatted for a chat message.</summary>
    public static async Task<string> AgendaAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId,
        DateTime fromLocal, DateTime toLocal, CancellationToken ct)
    {
        var connection = await CalendarAccountAsync(db, userId, ct);
        if (connection is null) return ReconnectMessage;
        var tz = await TimezoneAsync(db, userId, ct);
        string Utc(DateTime local) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz), TimeSpan.Zero).ToString("O");
        var url = Api + $"?singleEvents=true&orderBy=startTime&maxResults=25&timeMin={Uri.EscapeDataString(Utc(fromLocal))}&timeMax={Uri.EscapeDataString(Utc(toLocal))}";
        try
        {
            using var doc = await GmailIntegrationModule.GmailApiForAsync(userId, connection, url, HttpMethod.Get, null, db, protector, config, clients, ct);
            var lines = new List<string>();
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var e in items.EnumerateArray())
                {
                    var title = e.TryGetProperty("summary", out var sm) ? sm.GetString() : "(no title)";
                    var start = e.GetProperty("start");
                    lines.Add(start.TryGetProperty("dateTime", out var dt) && DateTimeOffset.TryParse(dt.GetString(), out var when)
                        ? $"• {TimeZoneInfo.ConvertTime(when, tz):ddd d MMM, h:mm tt} - {title}"
                        : $"• {(start.TryGetProperty("date", out var d) ? d.GetString() : "")} (all day) - {title}");
                }
            return lines.Count == 0 ? "📅 Nothing on your calendar in that time." : "📅 Your calendar:\n" + string.Join("\n", lines);
        }
        catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403) { return ReconnectMessage; }
        catch (GmailIntegrationException ex) { return "ℹ️ I couldn't read your calendar: " + ex.Message; }
    }
}
