using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Meetings;

/// <summary>Calendly API v2 client. Use OAuth access tokens for multi-user apps or personal access tokens for internal use.</summary>
public sealed class CalendlyIntegration(HttpClient http)
{
    private const string Base = "https://api.calendly.com/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CalendlyConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "users/me");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var envelope = await response.Content.ReadFromJsonAsync<CalendlyResourceEnvelope<CalendlyUser>>(Json, ct);
        var user = envelope?.Resource ?? throw new InvalidOperationException("Calendly returned no user resource.");
        return new(true, user.Uri, user.Name, user.Email, user.CurrentOrganization);
    }

    public async Task<CalendlyCollection<CalendlyEventType>> ListEventTypesAsync(string token, string userUri,
        int count = 20, string? pageToken = null, bool? active = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userUri)) throw new ArgumentException("Calendly user resource URI is required.", nameof(userUri));
        count = Math.Clamp(count, 1, 100);
        var query = $"event_types?user={Uri.EscapeDataString(userUri)}&count={count}";
        if (active.HasValue) query += $"&active={active.Value.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(pageToken)) query += $"&page_token={Uri.EscapeDataString(pageToken)}";
        using var request = Request(token, HttpMethod.Get, query);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<CalendlyCollection<CalendlyEventType>>(Json, ct)
            ?? new([], new(null, null, null));
    }

    public async Task<CalendlyCollection<CalendlyScheduledEvent>> ListScheduledEventsAsync(string token, string userUri,
        DateTimeOffset? minStart = null, DateTimeOffset? maxStart = null, string status = "active",
        int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userUri)) throw new ArgumentException("Calendly user resource URI is required.", nameof(userUri));
        if (status is not ("active" or "canceled")) throw new ArgumentException("Status must be active or canceled.", nameof(status));
        if (minStart.HasValue && maxStart.HasValue && maxStart <= minStart) throw new ArgumentException("Maximum start time must be after minimum start time.");
        count = Math.Clamp(count, 1, 100);
        var query = $"scheduled_events?user={Uri.EscapeDataString(userUri)}&count={count}&status={status}&sort=start_time:asc";
        if (minStart.HasValue) query += $"&min_start_time={Uri.EscapeDataString(minStart.Value.UtcDateTime.ToString("O"))}";
        if (maxStart.HasValue) query += $"&max_start_time={Uri.EscapeDataString(maxStart.Value.UtcDateTime.ToString("O"))}";
        if (!string.IsNullOrWhiteSpace(pageToken)) query += $"&page_token={Uri.EscapeDataString(pageToken)}";
        using var request = Request(token, HttpMethod.Get, query);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<CalendlyCollection<CalendlyScheduledEvent>>(Json, ct)
            ?? new([], new(null, null, null));
    }

    public async Task CancelScheduledEventAsync(string token, string eventUuid, string? reason = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventUuid)) throw new ArgumentException("Calendly scheduled event UUID is required.", nameof(eventUuid));
        using var request = Request(token, HttpMethod.Post, $"scheduled_events/{Uri.EscapeDataString(eventUuid)}/cancellation");
        request.Content = JsonContent.Create(new { reason }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Calendly bearer token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Calendly returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record CalendlyConnection(bool Connected, string? UserUri = null, string? Name = null, string? Email = null, string? OrganizationUri = null);
public sealed record CalendlyResourceEnvelope<T>(T? Resource);
public sealed record CalendlyUser(string? Uri, string? Name, string? Email, string? CurrentOrganization);
public sealed record CalendlyCollection<T>(IReadOnlyList<T>? Collection, CalendlyPagination? Pagination);
public sealed record CalendlyPagination(string? NextPage, string? NextPageToken, int? Count);
public sealed record CalendlyEventType(string? Uri, string? Name, string? SchedulingUrl, string? Duration, string? Status, string? Kind);
public sealed record CalendlyScheduledEvent(string? Uri, string? Name, string? Status, DateTimeOffset? StartTime, DateTimeOffset? EndTime, string? EventType);
