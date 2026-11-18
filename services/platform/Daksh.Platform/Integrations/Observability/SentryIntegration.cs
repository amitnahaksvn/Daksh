using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Observability;

/// <summary>Sentry API connector. Supply an organization-scoped auth token with only required scopes.</summary>
public sealed class SentryIntegration(HttpClient http)
{
    private const string Base = "https://sentry.io/api/0/";

    public async Task<SentryConnection> ProbeAsync(string token, CancellationToken ct)
    {
        using var req = Request(token, HttpMethod.Get, "auth/");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Sentry returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var username = root.TryGetProperty("user", out var user) && user.TryGetProperty("username", out var u) ? u.GetString() : null;
        return new(true, username);
    }

    public async Task<IReadOnlyList<SentryIssue>> ListIssuesAsync(string token, string organization, string project = "all",
        int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(organization)) throw new ArgumentException("Sentry organization slug is required.", nameof(organization));
        limit = Math.Clamp(limit, 1, 100);
        var path = $"organizations/{Uri.EscapeDataString(organization)}/issues/?project={Uri.EscapeDataString(project)}&limit={limit}&query=is%3Aunresolved";
        using var req = Request(token, HttpMethod.Get, path);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<List<SentryIssue>>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct) ?? [];
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Sentry auth token is required.", nameof(token));
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Sentry returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record SentryConnection(bool Connected, string? Username = null, string? Error = null);
public sealed record SentryIssue(string Id, string Title, string? Culprit, string? Status, string? Permalink);
