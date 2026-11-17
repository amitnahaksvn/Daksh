using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.DeveloperPlatforms;

/// <summary>
/// GitHub REST API integration. Credentials are supplied per invocation so callers can source them
/// from the encrypted integration store; this class never persists or logs tokens.
/// </summary>
public sealed class GitHubIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Api = "https://api.github.com/";

    public async Task<GitHubConnectionResult> ProbeAsync(string token, CancellationToken ct)
    {
        using var request = Create(token, HttpMethod.Get, "user");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new(false, Error: await ReadError(response, ct));
        var user = await response.Content.ReadFromJsonAsync<GitHubUser>(Json, ct);
        return new(true, user?.Login, user?.Name);
    }

    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(
        string token, string visibility = "all", int perPage = 30, CancellationToken ct = default)
    {
        if (visibility is not ("all" or "public" or "private"))
            throw new ArgumentException("Visibility must be all, public or private.", nameof(visibility));
        perPage = Math.Clamp(perPage, 1, 100);
        using var request = Create(token, HttpMethod.Get, $"user/repos?visibility={Uri.EscapeDataString(visibility)}&per_page={perPage}&sort=updated");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<GitHubRepository>>(Json, ct) ?? [];
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(
        string token, string owner, string repository, int perPage = 30, CancellationToken ct = default)
    {
        ValidateSegment(owner, nameof(owner));
        ValidateSegment(repository, nameof(repository));
        perPage = Math.Clamp(perPage, 1, 100);
        using var request = Create(token, HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/issues?state=open&per_page={perPage}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var items = await response.Content.ReadFromJsonAsync<List<GitHubIssue>>(Json, ct) ?? [];
        // GitHub's issues endpoint also returns pull requests; callers asking for issues should not receive those.
        return items.Where(i => i.PullRequest is null).ToArray();
    }

    public async Task<GitHubIssue> CreateIssueAsync(
        string token, string owner, string repository, string title, string body,
        IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        ValidateSegment(owner, nameof(owner));
        ValidateSegment(repository, nameof(repository));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Issue title is required.", nameof(title));
        var payload = new { title = title.Trim(), body, labels = labels ?? [] };
        using var request = Create(token, HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/issues");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<GitHubIssue>(Json, ct))
            ?? throw new InvalidOperationException("GitHub returned an empty issue response.");
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> ListPullRequestsAsync(
        string token, string owner, string repository, int perPage = 30, CancellationToken ct = default)
    {
        ValidateSegment(owner, nameof(owner));
        ValidateSegment(repository, nameof(repository));
        perPage = Math.Clamp(perPage, 1, 100);
        using var request = Create(token, HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pulls?state=open&per_page={perPage}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<GitHubPullRequest>>(Json, ct) ?? [];
    }

    private static HttpRequestMessage Create(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("GitHub token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Api), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Daksh-Integration/1.0");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("A single path segment is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await ReadError(response, ct), null, response.StatusCode);
    }

    private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        // Avoid including request headers or credential values in error messages.
        return $"GitHub API returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body[..Math.Min(body.Length, 500)]}";
    }

    private sealed record GitHubUser(string Login, string? Name);
}

public sealed record GitHubConnectionResult(bool Connected, string? Login = null, string? DisplayName = null, string? Error = null);

public sealed record GitHubRepository(
    long Id, string Name, string FullName, string? Description,
    [property: JsonPropertyName("html_url")] string Url,
    [property: JsonPropertyName("private")] bool IsPrivate,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch);

public sealed record GitHubIssue(
    long Number, string Title, string State,
    [property: JsonPropertyName("html_url")] string Url,
    [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

public sealed record GitHubPullRequest(
    long Number, string Title, string State,
    [property: JsonPropertyName("html_url")] string Url,
    [property: JsonPropertyName("draft")] bool Draft);
