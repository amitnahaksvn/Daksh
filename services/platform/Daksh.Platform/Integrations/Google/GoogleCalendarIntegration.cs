using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Google;

/// <summary>Google Calendar API v3 client. Supply a user OAuth access token with Calendar scopes on each call.</summary>
public sealed class GoogleCalendarIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Base = "https://www.googleapis.com/calendar/v3/";

    public async Task<GoogleCalendarConnection> ProbeAsync(string accessToken, CancellationToken ct)
    {
        using var req = Request(accessToken, HttpMethod.Get, "users/me/calendarList?maxResults=1");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Google Calendar returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        return new(true);
    }

    public async Task<GoogleCalendarPage> ListEventsAsync(string accessToken, string calendarId,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? pageToken = null, CancellationToken ct = default)
    {
        var query = new List<string> { "singleEvents=true", "orderBy=startTime", "maxResults=250" };
        if (from.HasValue) query.Add("timeMin=" + Uri.EscapeDataString(from.Value.ToString("O")));
        if (to.HasValue) query.Add("timeMax=" + Uri.EscapeDataString(to.Value.ToString("O")));
        if (!string.IsNullOrWhiteSpace(pageToken)) query.Add("pageToken=" + Uri.EscapeDataString(pageToken));
        var path = $"calendars/{Uri.EscapeDataString(calendarId)}/events?{string.Join("&", query)}";
        using var req = Request(accessToken, HttpMethod.Get, path);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<GoogleCalendarPage>(Json, ct) ?? new([]);
    }

    public async Task<GoogleCalendarEvent> CreateEventAsync(string accessToken, string calendarId,
        GoogleCalendarEventDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Summary)) throw new ArgumentException("Event summary is required.", nameof(draft));
        if (draft.Start is null || draft.End is null) throw new ArgumentException("Event start and end are required.", nameof(draft));
        var path = $"calendars/{Uri.EscapeDataString(calendarId)}/events?sendUpdates=all";
        using var req = Request(accessToken, HttpMethod.Post, path);
        req.Content = JsonContent.Create(new { summary = draft.Summary, description = draft.Description, location = draft.Location, start = draft.Start, end = draft.End }, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return (await res.Content.ReadFromJsonAsync<GoogleCalendarEvent>(Json, ct))
            ?? throw new InvalidOperationException("Google Calendar returned an empty event.");
    }

    public async Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct = default)
    {
        using var req = Request(accessToken, HttpMethod.Delete,
            $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}?sendUpdates=all");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        await EnsureSuccess(res, ct);
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Google OAuth access token is required.", nameof(token));
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google Calendar returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record GoogleCalendarConnection(bool Connected, string? Error = null);
public sealed record GoogleCalendarPage([property: JsonPropertyName("items")] IReadOnlyList<GoogleCalendarEvent>? Items,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken = null,
    [property: JsonPropertyName("nextSyncToken")] string? NextSyncToken = null);
public sealed record GoogleCalendarEvent(string Id, string? Summary, string? HtmlLink,
    [property: JsonPropertyName("status")] string? Status = null);
public sealed record GoogleCalendarEventDraft(string Summary, object Start, object End, string? Description = null, string? Location = null);
