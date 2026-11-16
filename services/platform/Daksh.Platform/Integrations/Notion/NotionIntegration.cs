using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Notion;

/// <summary>Notion public API client. Supply a token for an integration installed in the target workspace.</summary>
public sealed class NotionIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Base = "https://api.notion.com/v1/";

    public async Task<NotionConnection> ProbeAsync(string token, CancellationToken ct)
    {
        using var req = Request(token, HttpMethod.Get, "users/me");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Notion returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        var user = await res.Content.ReadFromJsonAsync<NotionUser>(Json, ct);
        return new(true, user?.Name, user?.Type);
    }

    public async Task<NotionPageResult> SearchAsync(string token, string query, string? cursor = null, CancellationToken ct = default)
    {
        using var req = Request(token, HttpMethod.Post, "search");
        req.Content = JsonContent.Create(new { query, page_size = 100, start_cursor = cursor }, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<NotionPageResult>(Json, ct) ?? new([]);
    }

    public async Task<NotionObject> CreatePageAsync(string token, string parentPageId, string title, string? paragraph = null, CancellationToken ct = default)
    {
        if (!Guid.TryParse(parentPageId, out _) && string.IsNullOrWhiteSpace(parentPageId))
            throw new ArgumentException("A parent page or database ID is required.", nameof(parentPageId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Page title is required.", nameof(title));
        var blocks = string.IsNullOrWhiteSpace(paragraph) ? Array.Empty<object>() : new object[] { new { @object = "block", type = "paragraph", paragraph = new { rich_text = new[] { new { type = "text", text = new { content = paragraph } } } } } };
        using var req = Request(token, HttpMethod.Post, "pages");
        req.Content = JsonContent.Create(new { parent = new { page_id = parentPageId }, properties = new { title = new[] { new { text = new { content = title } } } }, children = blocks }, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return (await res.Content.ReadFromJsonAsync<NotionObject>(Json, ct)) ?? throw new InvalidOperationException("Notion returned no page.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Notion token is required.", nameof(token));
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Notion-Version", "2022-06-28");
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Notion returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record NotionConnection(bool Connected, string? Name = null, string? Type = null, string? Error = null);
public sealed record NotionUser(string? Id, string? Name, string? Type);
public sealed record NotionPageResult(IReadOnlyList<NotionObject>? Results, bool HasMore = false, string? NextCursor = null);
public sealed record NotionObject(string Id, string? Url, string? Object);
