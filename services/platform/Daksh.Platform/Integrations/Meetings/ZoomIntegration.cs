using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Meetings;

/// <summary>Zoom REST API client. Supply an OAuth or Server-to-Server OAuth access token per call.</summary>
public sealed class ZoomIntegration(HttpClient http)
{
    private const string Base = "https://api.zoom.us/v2/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ZoomConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "users/me");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var user = await response.Content.ReadFromJsonAsync<ZoomUser>(Json, ct);
        return new(true, user?.Id, user?.Email, user?.FirstName, user?.LastName);
    }

    public async Task<ZoomMeetingPage> ListMeetingsAsync(string token, string userId = "me", int pageSize = 30,
        string? nextPageToken = null, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 300);
        var path = $"users/{Uri.EscapeDataString(userId)}/meetings?page_size={pageSize}&type=scheduled";
        if (!string.IsNullOrWhiteSpace(nextPageToken)) path += $"&next_page_token={Uri.EscapeDataString(nextPageToken)}";
        using var request = Request(token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZoomMeetingPage>(Json, ct) ?? new([], null, 0);
    }

    public async Task<ZoomMeeting> CreateMeetingAsync(string token, string topic, DateTimeOffset start,
        int durationMinutes = 30, string timeZone = "UTC", string? agenda = null, string userId = "me",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("Meeting topic is required.", nameof(topic));
        if (durationMinutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(durationMinutes));
        var payload = new { topic, type = 2, start_time = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            duration = durationMinutes, timezone = timeZone, agenda };
        using var request = Request(token, HttpMethod.Post, $"users/{Uri.EscapeDataString(userId)}/meetings");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZoomMeeting>(Json, ct)
            ?? throw new InvalidOperationException("Zoom returned no meeting.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Zoom access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Zoom returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record ZoomConnection(bool Connected, string? UserId = null, string? Email = null, string? FirstName = null, string? LastName = null);
public sealed record ZoomUser(string? Id, string? Email, string? FirstName, string? LastName);
public sealed record ZoomMeetingPage(IReadOnlyList<ZoomMeeting>? Meetings, string? NextPageToken, int TotalRecords);
public sealed record ZoomMeeting(long Id, string? Uuid, string? Topic, string? JoinUrl, string? StartUrl, DateTimeOffset? StartTime, int Duration);
