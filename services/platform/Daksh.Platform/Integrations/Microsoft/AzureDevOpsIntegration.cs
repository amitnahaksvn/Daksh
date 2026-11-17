using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Microsoft;

/// <summary>
/// Azure DevOps Services REST 7.1 client. Supports PAT or Entra bearer tokens supplied per call.
/// Credentials are deliberately not persisted or logged by this client.
/// </summary>
public sealed class AzureDevOpsIntegration(HttpClient http)
{
    private const string ApiVersion = "7.1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AzureDevOpsConnection> ProbeAsync(
        string organization, string token, bool isPersonalAccessToken = true, CancellationToken ct = default)
    {
        ValidateOrganization(organization);
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Get,
            $"_apis/projects?$top=1&api-version={ApiVersion}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var projects = await ReadPageAsync<AzureDevOpsProject>(response, ct);
        return new(true, organization, projects.Count);
    }

    public async Task<AzureDevOpsPage<AzureDevOpsProject>> ListProjectsAsync(
        string organization, string token, bool isPersonalAccessToken = true, int top = 100,
        string? continuationToken = null, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 1000);
        var path = $"_apis/projects?$top={top}&api-version={ApiVersion}";
        if (!string.IsNullOrWhiteSpace(continuationToken))
            path += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await ReadPageAsync<AzureDevOpsProject>(response, ct);
    }

    public async Task<IReadOnlyList<AzureDevOpsRepository>> ListRepositoriesAsync(
        string organization, string project, string token, bool isPersonalAccessToken = true,
        CancellationToken ct = default)
    {
        var path = $"{Uri.EscapeDataString(project)}/_apis/git/repositories?api-version={ApiVersion}";
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var page = await response.Content.ReadFromJsonAsync<AzureDevOpsPage<AzureDevOpsRepository>>(Json, ct);
        return page?.Value ?? [];
    }

    public async Task<IReadOnlyList<AzureDevOpsWorkItemReference>> QueryWorkItemsAsync(
        string organization, string project, string token, string wiql,
        bool isPersonalAccessToken = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wiql)) throw new ArgumentException("WIQL query is required.", nameof(wiql));
        var path = $"{Uri.EscapeDataString(project)}/_apis/wit/wiql?api-version={ApiVersion}";
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Post, path);
        request.Content = JsonContent.Create(new { query = wiql }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<AzureDevOpsWiqlResult>(Json, ct);
        return result?.WorkItems ?? [];
    }

    public async Task<AzureDevOpsWorkItem> GetWorkItemAsync(
        string organization, string project, int workItemId, string token,
        bool isPersonalAccessToken = true, CancellationToken ct = default)
    {
        if (workItemId <= 0) throw new ArgumentOutOfRangeException(nameof(workItemId));
        var path = $"{Uri.EscapeDataString(project)}/_apis/wit/workitems/{workItemId}?api-version={ApiVersion}";
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<AzureDevOpsWorkItem>(Json, ct)
            ?? throw new InvalidOperationException("Azure DevOps returned an empty work item.");
    }

    public async Task<AzureDevOpsWorkItem> CreateWorkItemAsync(
        string organization, string project, string workItemType, string title, string token,
        string? description = null, bool isPersonalAccessToken = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workItemType)) throw new ArgumentException("Work item type is required.", nameof(workItemType));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Work item title is required.", nameof(title));
        var path = $"{Uri.EscapeDataString(project)}/_apis/wit/workitems/$" +
            $"{Uri.EscapeDataString(workItemType)}?api-version={ApiVersion}";
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title.Trim() }
        };
        if (!string.IsNullOrWhiteSpace(description))
            patch.Add(new { op = "add", path = "/fields/System.Description", value = description });
        using var request = CreateRequest(organization, token, isPersonalAccessToken, HttpMethod.Post, path);
        request.Content = new StringContent(JsonSerializer.Serialize(patch, Json), Encoding.UTF8, "application/json-patch+json");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<AzureDevOpsWorkItem>(Json, ct)
            ?? throw new InvalidOperationException("Azure DevOps returned an empty work item.");
    }

    private static async Task<AzureDevOpsPage<T>> ReadPageAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var page = await response.Content.ReadFromJsonAsync<AzureDevOpsPage<T>>(Json, ct);
        response.Headers.TryGetValues("x-ms-continuationtoken", out var values);
        var continuationToken = values?.FirstOrDefault() ?? page?.ContinuationToken;
        return new(page?.Value ?? [], page?.Count ?? 0, continuationToken);
    }

    private static HttpRequestMessage CreateRequest(
        string organization, string token, bool isPersonalAccessToken, HttpMethod method, string path)
    {
        ValidateOrganization(organization);
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Azure DevOps token is required.", nameof(token));
        var request = new HttpRequestMessage(method,
            new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{path}"));
        var encoded = isPersonalAccessToken
            ? Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"))
            : token;
        request.Headers.Authorization = new AuthenticationHeaderValue(isPersonalAccessToken ? "Basic" : "Bearer", encoded);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateOrganization(string organization)
    {
        if (string.IsNullOrWhiteSpace(organization) ||
            organization.Contains('/') || organization.Contains('\\') ||
            organization.Contains('?') || organization.Contains('#'))
            throw new ArgumentException("A valid Azure DevOps organization name is required.", nameof(organization));
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Azure DevOps returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body[..Math.Min(body.Length, 500)]}",
                null, response.StatusCode);
        }
    }
}

public sealed record AzureDevOpsConnection(bool Connected, string Organization, int VisibleProjectCount);
public sealed record AzureDevOpsPage<T>(
    [property: JsonPropertyName("value")] IReadOnlyList<T>? Value,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken);
public sealed record AzureDevOpsProject(string Id, string Name, string State, string? Url);
public sealed record AzureDevOpsRepository(string Id, string Name, string? Url, string? RemoteUrl, string? DefaultBranch);
public sealed record AzureDevOpsWiqlResult(
    [property: JsonPropertyName("workItems")] IReadOnlyList<AzureDevOpsWorkItemReference>? WorkItems);
public sealed record AzureDevOpsWorkItemReference(int Id, string? Url);
public sealed record AzureDevOpsWorkItem(int Id, int Rev, JsonElement Fields, string? Url);
