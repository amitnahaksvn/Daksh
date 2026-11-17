using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Atlassian;

/// <summary>Confluence Cloud REST API v2 client. Atlassian email/API token are supplied per call.</summary>
public sealed class ConfluenceIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ConfluenceConnection> ProbeAsync(string siteUrl, string email, string apiToken, CancellationToken ct = default)
    {
        using var request = CreateRequest(siteUrl, email, apiToken, HttpMethod.Get, "/wiki/api/v2/spaces?limit=1");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<ConfluencePage<ConfluenceSpace>>(Json, ct);
        return new(true, result?.Value?.Count ?? 0);
    }

    public async Task<ConfluencePage<ConfluenceSpace>> ListSpacesAsync(
        string siteUrl, string email, string apiToken, int limit = 50, string? cursor = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 250);
        var path = $"/wiki/api/v2/spaces?limit={limit}";
        if (!string.IsNullOrWhiteSpace(cursor)) path += $"&cursor={Uri.EscapeDataString(cursor)}";
        using var request = CreateRequest(siteUrl, email, apiToken, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ConfluencePage<ConfluenceSpace>>(Json, ct)
            ?? new([], null);
    }

    public async Task<ConfluencePage<ConfluencePageSummary>> ListPagesAsync(
        string siteUrl, string email, string apiToken, long? spaceId = null, string? title = null,
        int limit = 50, string? cursor = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 250);
        var query = new List<string> { $"limit={limit}" };
        if (spaceId.HasValue) query.Add($"space-id={spaceId.Value}");
        if (!string.IsNullOrWhiteSpace(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var request = CreateRequest(siteUrl, email, apiToken, HttpMethod.Get,
            "/wiki/api/v2/pages?" + string.Join("&", query));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ConfluencePage<ConfluencePageSummary>>(Json, ct)
            ?? new([], null);
    }

    public async Task<ConfluencePageSummary> CreatePageAsync(
        string siteUrl, string email, string apiToken, long spaceId, string title, string htmlBody,
        long? parentId = null, CancellationToken ct = default)
    {
        if (spaceId <= 0) throw new ArgumentOutOfRangeException(nameof(spaceId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Confluence page title is required.", nameof(title));
        var payload = new Dictionary<string, object?>
        {
            ["spaceId"] = spaceId.ToString(),
            ["status"] = "current",
            ["title"] = title.Trim(),
            ["body"] = new { representation = "storage", value = htmlBody ?? string.Empty }
        };
        if (parentId.HasValue) payload["parentId"] = parentId.Value.ToString();
        using var request = CreateRequest(siteUrl, email, apiToken, HttpMethod.Post, "/wiki/api/v2/pages");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ConfluencePageSummary>(Json, ct)
            ?? throw new InvalidOperationException("Confluence returned an empty page response.");
    }

    private static HttpRequestMessage CreateRequest(
        string siteUrl, string email, string apiToken, HttpMethod method, string path)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) ||
            site.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(site.Host) ||
            !site.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid https://your-site.atlassian.net URL is required.", nameof(siteUrl));
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Atlassian account email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(apiToken)) throw new ArgumentException("Atlassian API token is required.", nameof(apiToken));
        var root = new UriBuilder(site) { Path = "/", Query = "", Fragment = "" }.Uri;
        var request = new HttpRequestMessage(method, new Uri(root, path));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Confluence returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body[..Math.Min(body.Length, 500)]}",
                null, response.StatusCode);
        }
    }
}

public sealed record ConfluenceConnection(bool Connected, int VisibleSpaceCount);
public sealed record ConfluencePage<T>(
    [property: JsonPropertyName("results")] IReadOnlyList<T>? Value,
    [property: JsonPropertyName("_links")] ConfluenceLinks? Links);
public sealed record ConfluenceLinks(string? Next, string? Base);
public sealed record ConfluenceSpace(string Id, string Key, string Name, string Type, string Status, string? WebUrl);
public sealed record ConfluencePageSummary(string Id, string Status, string Title, string? WebUrl, string? SpaceId, string? ParentId);
