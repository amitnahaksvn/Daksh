using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.DeveloperPlatforms;

/// <summary>Bitbucket Cloud REST API 2.0 client using app passwords or API tokens supplied per call.</summary>
public sealed class BitbucketIntegration(HttpClient http)
{
    private const string ApiBase = "https://api.bitbucket.org/2.0/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BitbucketConnection> ProbeAsync(string username, string token, CancellationToken ct = default)
    {
        using var request = CreateRequest(username, token, HttpMethod.Get, "user");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var user = await response.Content.ReadFromJsonAsync<BitbucketUser>(Json, ct);
        return new(true, user?.Uuid, user?.DisplayName, user?.Nickname);
    }

    public async Task<BitbucketPage<BitbucketRepository>> ListRepositoriesAsync(
        string username, string token, string workspace, int pageLength = 50,
        string? nextUrl = null, CancellationToken ct = default)
    {
        ValidateSegment(workspace, nameof(workspace));
        pageLength = Math.Clamp(pageLength, 1, 100);
        var path = string.IsNullOrWhiteSpace(nextUrl)
            ? $"repositories/{Uri.EscapeDataString(workspace)}?pagelen={pageLength}&sort=-updated_on"
            : ValidateNextUrl(nextUrl);
        using var request = CreateRequest(username, token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BitbucketPage<BitbucketRepository>>(Json, ct)
            ?? new([], null, null);
    }

    public async Task<BitbucketPage<BitbucketPullRequest>> ListPullRequestsAsync(
        string username, string token, string workspace, string repository,
        string state = "OPEN", int pageLength = 50, string? nextUrl = null, CancellationToken ct = default)
    {
        ValidateSegment(workspace, nameof(workspace));
        ValidateSegment(repository, nameof(repository));
        if (state is not ("OPEN" or "MERGED" or "DECLINED" or "SUPERSEDED"))
            throw new ArgumentException("Unsupported Bitbucket pull-request state.", nameof(state));
        pageLength = Math.Clamp(pageLength, 1, 100);
        var path = string.IsNullOrWhiteSpace(nextUrl)
            ? $"repositories/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(repository)}/pullrequests?state={state}&pagelen={pageLength}"
            : ValidateNextUrl(nextUrl);
        using var request = CreateRequest(username, token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BitbucketPage<BitbucketPullRequest>>(Json, ct)
            ?? new([], null, null);
    }

    public async Task<BitbucketPage<BitbucketIssue>> ListIssuesAsync(
        string username, string token, string workspace, string repository,
        int pageLength = 50, string? nextUrl = null, CancellationToken ct = default)
    {
        ValidateSegment(workspace, nameof(workspace));
        ValidateSegment(repository, nameof(repository));
        pageLength = Math.Clamp(pageLength, 1, 100);
        var path = string.IsNullOrWhiteSpace(nextUrl)
            ? $"repositories/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(repository)}/issues?pagelen={pageLength}&sort=-updated_on"
            : ValidateNextUrl(nextUrl);
        using var request = CreateRequest(username, token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BitbucketPage<BitbucketIssue>>(Json, ct)
            ?? new([], null, null);
    }

    public async Task<BitbucketIssue> CreateIssueAsync(
        string username, string token, string workspace, string repository, string title,
        string? content = null, CancellationToken ct = default)
    {
        ValidateSegment(workspace, nameof(workspace));
        ValidateSegment(repository, nameof(repository));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Issue title is required.", nameof(title));
        var payload = new
        {
            title = title.Trim(),
            content = string.IsNullOrWhiteSpace(content) ? null : new { raw = content, markup = "markdown" }
        };
        using var request = CreateRequest(username, token, HttpMethod.Post,
            $"repositories/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(repository)}/issues");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BitbucketIssue>(Json, ct)
            ?? throw new InvalidOperationException("Bitbucket returned an empty issue response.");
    }

    private static HttpRequestMessage CreateRequest(string username, string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Bitbucket username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Bitbucket token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(ApiBase), path));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string ValidateNextUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.bitbucket.org", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/2.0/", StringComparison.Ordinal))
            throw new ArgumentException("Bitbucket pagination URL is not a trusted Bitbucket API URL.", nameof(value));
        return uri.ToString();
    }

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Bitbucket workspace/repository slug is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Bitbucket returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body[..Math.Min(body.Length, 500)]}",
                null, response.StatusCode);
        }
    }
}

public sealed record BitbucketConnection(bool Connected, string? Uuid, string? DisplayName, string? Nickname);
public sealed record BitbucketUser(string? Uuid, string? DisplayName, string? Nickname);
public sealed record BitbucketPage<T>(
    IReadOnlyList<T>? Values,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("size")] int? Size);
public sealed record BitbucketRepository(string Uuid, string Name, string Slug, string? FullName,
    string? Language, string? Mainbranch, string? WebUrl);
public sealed record BitbucketPullRequest(int Id, string Title, string State, string? Description, string? CreatedOn, string? UpdatedOn);
public sealed record BitbucketIssue(int Id, string Title, string State, string? Content, string? CreatedOn, string? UpdatedOn);
