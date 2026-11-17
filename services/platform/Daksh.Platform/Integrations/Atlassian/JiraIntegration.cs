using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Atlassian;

/// <summary>Jira Cloud REST v3 client. OAuth 3LO access tokens use the Atlassian API gateway URL.</summary>
public sealed class JiraIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JiraConnection> ProbeAsync(string cloudId, string accessToken, CancellationToken ct)
    {
        var url = ApiBase(cloudId) + "myself";
        using var req = Request(accessToken, HttpMethod.Get, url);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Jira returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        var user = await res.Content.ReadFromJsonAsync<JiraUser>(Json, ct);
        return new(true, user?.DisplayName, user?.AccountId);
    }

    public async Task<JiraSearchResult> SearchIssuesAsync(string cloudId, string accessToken, string jql, int maxResults = 50, CancellationToken ct = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 100);
        using var req = Request(accessToken, HttpMethod.Post, ApiBase(cloudId) + "search/jql");
        req.Content = JsonContent.Create(new { jql, maxResults, fields = new[] { "summary", "status", "assignee", "issuetype" } }, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<JiraSearchResult>(Json, ct) ?? new([]);
    }

    public async Task<JiraIssue> CreateIssueAsync(string cloudId, string accessToken, string projectKey, string issueType, string summary, string? description = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(issueType) || string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("Project key, issue type and summary are required.");
        var descriptionDoc = description is null ? null : new { type = "doc", version = 1, content = new object[] { new { type = "paragraph", content = new object[] { new { type = "text", text = description } } } } };
        var fields = new Dictionary<string, object?> { ["project"] = new { key = projectKey }, ["issuetype"] = new { name = issueType }, ["summary"] = summary };
        if (descriptionDoc is not null) fields["description"] = descriptionDoc;
        using var req = Request(accessToken, HttpMethod.Post, ApiBase(cloudId) + "issue");
        req.Content = JsonContent.Create(new { fields }, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        return (await res.Content.ReadFromJsonAsync<JiraIssue>(Json, ct)) ?? throw new InvalidOperationException("Jira returned no issue.");
    }

    private static string ApiBase(string cloudId)
    {
        if (string.IsNullOrWhiteSpace(cloudId) || cloudId.Contains('/') || cloudId.Contains('?'))
            throw new ArgumentException("A valid Atlassian cloud ID is required.", nameof(cloudId));
        return $"https://api.atlassian.com/ex/jira/{Uri.EscapeDataString(cloudId)}/rest/api/3/";
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string url)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Atlassian OAuth access token is required.", nameof(token));
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Jira returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record JiraConnection(bool Connected, string? DisplayName = null, string? AccountId = null, string? Error = null);
public sealed record JiraSearchResult(IReadOnlyList<JiraIssue>? Issues, bool IsLast = true, string? NextPageToken = null);
public sealed record JiraIssue(string Id, string Key, string? Self);
public sealed record JiraUser(string? AccountId, string? DisplayName);
