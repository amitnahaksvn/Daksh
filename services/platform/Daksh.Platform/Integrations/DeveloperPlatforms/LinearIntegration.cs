using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.DeveloperPlatforms;

/// <summary>Linear GraphQL API client. OAuth access tokens or personal API keys are supplied per call.</summary>
public sealed class LinearIntegration(HttpClient http)
{
    private const string Endpoint = "https://api.linear.app/graphql";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LinearConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var doc = await ExecuteAsync(token, "query { viewer { id name email } }", null, ct);
        var viewer = doc.RootElement.GetProperty("data").GetProperty("viewer");
        return new(true, viewer.GetProperty("id").GetString(), viewer.GetProperty("name").GetString(),
            viewer.TryGetProperty("email", out var email) ? email.GetString() : null);
    }

    public async Task<LinearIssuePage> ListIssuesAsync(string token, int first = 50, string? after = null, CancellationToken ct = default)
    {
        first = Math.Clamp(first, 1, 100);
        const string query = "query Issues($first: Int!, $after: String) { issues(first: $first, after: $after) { nodes { id identifier title description url } pageInfo { hasNextPage endCursor } } }";
        using var doc = await ExecuteAsync(token, query, new { first, after }, ct);
        var issues = doc.RootElement.GetProperty("data").GetProperty("issues");
        return issues.Deserialize<LinearIssuePage>(Json) ?? new([], new(false, null));
    }

    public async Task<LinearIssue> CreateIssueAsync(string token, string teamId, string title, string? description = null,
        string? assigneeId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("Linear team ID is required.", nameof(teamId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Issue title is required.", nameof(title));
        const string mutation = "mutation CreateIssue($input: IssueCreateInput!) { issueCreate(input: $input) { success issue { id identifier title description url } } }";
        var input = new Dictionary<string, object?> { ["teamId"] = teamId, ["title"] = title };
        if (!string.IsNullOrWhiteSpace(description)) input["description"] = description;
        if (!string.IsNullOrWhiteSpace(assigneeId)) input["assigneeId"] = assigneeId;
        using var doc = await ExecuteAsync(token, mutation, new { input }, ct);
        var result = doc.RootElement.GetProperty("data").GetProperty("issueCreate");
        if (!result.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Linear did not create the issue.");
        return result.GetProperty("issue").Deserialize<LinearIssue>(Json) ?? throw new InvalidOperationException("Linear returned no issue.");
    }

    private async Task<JsonDocument> ExecuteAsync(string token, string query, object? variables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Linear API token is required.", nameof(token));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { query, variables }, options: Json);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Linear returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var m) ? m.GetString() : "GraphQL operation failed.";
            document.Dispose();
            throw new HttpRequestException($"Linear GraphQL error: {message}");
        }
        return document;
    }
}

public sealed record LinearConnection(bool Connected, string? UserId = null, string? Name = null, string? Email = null);
public sealed record LinearIssuePage(IReadOnlyList<LinearIssue>? Nodes, LinearPageInfo PageInfo);
public sealed record LinearPageInfo(bool HasNextPage, string? EndCursor);
public sealed record LinearIssue(string Id, string? Identifier, string Title, string? Description, string? Url);
