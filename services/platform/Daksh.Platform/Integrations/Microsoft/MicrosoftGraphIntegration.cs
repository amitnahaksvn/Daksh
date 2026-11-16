using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Microsoft;

/// <summary>
/// Microsoft Graph v1.0 client for Outlook mail/calendar and OneDrive/SharePoint drive resources.
/// Tokens must be delegated or application tokens with the relevant Graph permissions.
/// </summary>
public sealed class MicrosoftGraphIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Base = "https://graph.microsoft.com/v1.0/";

    public async Task<MicrosoftGraphConnection> ProbeAsync(string token, CancellationToken ct)
    {
        using var req = Request(token, HttpMethod.Get, "me?$select=id,displayName,mail,userPrincipalName");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Microsoft Graph returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        var me = await res.Content.ReadFromJsonAsync<GraphUser>(Json, ct);
        return new(true, me?.DisplayName, me?.Mail ?? me?.UserPrincipalName);
    }

    public async Task<GraphCollection<GraphMessage>> ListMessagesAsync(string token, int top = 25, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 100);
        using var req = Request(token, HttpMethod.Get, $"me/messages?$top={top}&$orderby=receivedDateTime%20desc&$select=id,subject,from,receivedDateTime,isRead,webLink");
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<GraphCollection<GraphMessage>>(Json, ct) ?? new([]);
    }

    public async Task<GraphMessage> SendMailAsync(string token, string to, string subject, string body, bool html = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("Recipient is required.", nameof(to));
        var payload = new { message = new { subject, body = new { contentType = html ? "HTML" : "Text", content = body }, toRecipients = new[] { new { emailAddress = new { address = to } } } }, saveToSentItems = true };
        using var req = Request(token, HttpMethod.Post, "me/sendMail");
        req.Content = JsonContent.Create(payload, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return new GraphMessage("sent", subject, null, null, null, null);
    }

    public async Task<GraphCollection<GraphEvent>> ListCalendarEventsAsync(string token, DateTimeOffset? from = null, DateTimeOffset? to = null, int top = 50, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 100);
        var path = $"me/calendar/events?$top={top}&$orderby=start/dateTime";
        if (from.HasValue && to.HasValue)
            path = $"me/calendarView?startDateTime={Uri.EscapeDataString(from.Value.ToString("O"))}&endDateTime={Uri.EscapeDataString(to.Value.ToString("O"))}&$top={top}";
        using var req = Request(token, HttpMethod.Get, path);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<GraphCollection<GraphEvent>>(Json, ct) ?? new([]);
    }

    public async Task<GraphEvent> CreateCalendarEventAsync(string token, string subject, DateTimeOffset start, DateTimeOffset end, string timeZone = "UTC", string? body = null, CancellationToken ct = default)
    {
        if (end <= start) throw new ArgumentException("Event end must be after its start.");
        var payload = new { subject, body = new { contentType = "Text", content = body ?? "" }, start = new { dateTime = start.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone }, end = new { dateTime = end.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone } };
        using var req = Request(token, HttpMethod.Post, "me/events");
        req.Content = JsonContent.Create(payload, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return (await res.Content.ReadFromJsonAsync<GraphEvent>(Json, ct)) ?? throw new InvalidOperationException("Graph returned no event.");
    }

    public async Task<GraphCollection<GraphDriveItem>> ListDriveItemsAsync(string token, string driveId = "me/drive", string itemId = "root", CancellationToken ct = default)
    {
        var path = driveId == "me/drive"
            ? $"me/drive/items/{Uri.EscapeDataString(itemId)}/children"
            : $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/children";
        using var req = Request(token, HttpMethod.Get, path);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<GraphCollection<GraphDriveItem>>(Json, ct) ?? new([]);
    }

    public async Task<GraphCollection<GraphSite>> SearchSharePointSitesAsync(string token, string query, CancellationToken ct = default)
    {
        using var req = Request(token, HttpMethod.Get, $"sites?search={Uri.EscapeDataString(query)}");
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<GraphCollection<GraphSite>>(Json, ct) ?? new([]);
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Microsoft Graph access token is required.", nameof(token));
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Microsoft Graph returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record MicrosoftGraphConnection(bool Connected, string? DisplayName = null, string? Email = null, string? Error = null);
public sealed record GraphCollection<T>(IReadOnlyList<T>? Value, string? NextLink = null);
public sealed record GraphUser(string? Id, string? DisplayName, string? Mail, string? UserPrincipalName);
public sealed record GraphMessage(string Id, string? Subject, JsonElement? From, DateTimeOffset? ReceivedDateTime, bool? IsRead, string? WebLink);
public sealed record GraphEvent(string Id, string? Subject, JsonElement? Start, JsonElement? End, string? WebLink);
public sealed record GraphDriveItem(string Id, string? Name, string? WebUrl, long? Size);
public sealed record GraphSite(string Id, string? Name, string? WebUrl);
