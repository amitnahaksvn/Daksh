using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.DeveloperPlatforms;

/// <summary>GitLab REST API connector for gitlab.com. Tokens are supplied per call and never persisted or logged here.</summary>
public sealed class GitLabIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Api = "https://gitlab.com/api/v4/";

    public async Task<GitLabConnectionResult> ProbeAsync(string token, CancellationToken ct)
    {
        using var request = Create(token, HttpMethod.Get, "user");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new(false, Error: $"GitLab API returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        var user = await response.Content.ReadFromJsonAsync<GitLabUser>(Json, ct);
        return new(true, user?.Username, user?.Name);
    }

    public async Task<IReadOnlyList<GitLabProject>> ListProjectsAsync(
        string token, int perPage = 30, CancellationToken ct = default)
    {
        perPage = Math.Clamp(perPage, 1, 100);
        using var request = Create(token, HttpMethod.Get, $"projects?membership=true&order_by=last_activity_at&per_page={perPage}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<GitLabProject>>(Json, ct) ?? [];
    }

    public async Task<GitLabIssue> CreateIssueAsync(
        string token, long projectId, string title, string? description = null, CancellationToken ct = default)
    {
        if (projectId <= 0) throw new ArgumentOutOfRangeException(nameof(projectId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Issue title is required.", nameof(title));
        using var request = Create(token, HttpMethod.Post, $"projects/{projectId}/issues");
        request.Content = JsonContent.Create(new { title = title.Trim(), description }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<GitLabIssue>(Json, ct))
            ?? throw new InvalidOperationException("GitLab returned an empty issue response.");
    }

    private static HttpRequestMessage Create(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("GitLab token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Api), path));
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitLab API returned {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
    }

    private sealed record GitLabUser(string Username, string? Name);
}

public sealed record GitLabConnectionResult(bool Connected, string? Username = null, string? DisplayName = null, string? Error = null);
public sealed record GitLabProject(long Id, string Name, string PathWithNamespace,
    [property: JsonPropertyName("web_url")] string Url,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("visibility")] string Visibility);
public sealed record GitLabIssue(long Id, int Iid, string Title, string State,
    [property: JsonPropertyName("web_url")] string Url);
