using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Cloud;

/// <summary>Vercel REST API client for team-scoped project and deployment discovery.</summary>
public sealed class VercelIntegration(HttpClient http)
{
    private const string Base = "https://api.vercel.com/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VercelUser> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "v2/user");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<VercelUser>(Json, ct)
            ?? throw new InvalidOperationException("Vercel returned an empty user response.");
    }

    public async Task<VercelProjectPage> ListProjectsAsync(string token, string? teamId = null,
        string? search = null, int limit = 20, string? from = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(teamId)) query.Add("teamId=" + Uri.EscapeDataString(teamId));
        if (!string.IsNullOrWhiteSpace(search)) query.Add("search=" + Uri.EscapeDataString(search));
        if (!string.IsNullOrWhiteSpace(from)) query.Add("from=" + Uri.EscapeDataString(from));
        using var request = Request(token, HttpMethod.Get, "v9/projects?" + string.Join("&", query));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<VercelProjectPage>(Json, ct)
            ?? throw new InvalidOperationException("Vercel returned an empty projects response.");
    }

    public async Task<VercelProject> GetProjectAsync(string token, string projectIdOrName,
        string? teamId = null, CancellationToken ct = default)
    {
        ValidateId(projectIdOrName, nameof(projectIdOrName));
        var path = "v9/projects/" + Uri.EscapeDataString(projectIdOrName);
        if (!string.IsNullOrWhiteSpace(teamId)) path += "?teamId=" + Uri.EscapeDataString(teamId);
        using var request = Request(token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<VercelProject>(Json, ct)
            ?? throw new InvalidOperationException("Vercel returned an empty project.");
    }

    public async Task<VercelDeploymentPage> ListDeploymentsAsync(string token, string? projectId = null,
        string? teamId = null, string? target = null, int limit = 20, string? since = null,
        string? until = null, string? from = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(projectId)) query.Add("projectId=" + Uri.EscapeDataString(projectId));
        if (!string.IsNullOrWhiteSpace(teamId)) query.Add("teamId=" + Uri.EscapeDataString(teamId));
        if (!string.IsNullOrWhiteSpace(target)) query.Add("target=" + Uri.EscapeDataString(target));
        if (!string.IsNullOrWhiteSpace(since)) query.Add("since=" + Uri.EscapeDataString(since));
        if (!string.IsNullOrWhiteSpace(until)) query.Add("until=" + Uri.EscapeDataString(until));
        if (!string.IsNullOrWhiteSpace(from)) query.Add("from=" + Uri.EscapeDataString(from));
        using var request = Request(token, HttpMethod.Get, "v6/deployments?" + string.Join("&", query));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<VercelDeploymentPage>(Json, ct)
            ?? throw new InvalidOperationException("Vercel returned an empty deployments response.");
    }

    public async Task<VercelDeployment> GetDeploymentAsync(string token, string deploymentIdOrUrl,
        string? teamId = null, CancellationToken ct = default)
    {
        ValidateId(deploymentIdOrUrl, nameof(deploymentIdOrUrl));
        var path = "v13/deployments/" + Uri.EscapeDataString(deploymentIdOrUrl);
        if (!string.IsNullOrWhiteSpace(teamId)) path += "?teamId=" + Uri.EscapeDataString(teamId);
        using var request = Request(token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<VercelDeployment>(Json, ct)
            ?? throw new InvalidOperationException("Vercel returned an empty deployment.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Vercel access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Vercel identifier is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Vercel returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record VercelUser(VercelUserInfo? User, string? Token);
public sealed record VercelUserInfo(string? Id, string? Username, string? Email, string? Name);
public sealed record VercelProjectPage(IReadOnlyList<VercelProject>? Projects, string? PaginationNext);
public sealed record VercelProject(string Id, string Name, string? Framework, string? NodeVersion,
    string? RootDirectory, string? CreatedAt, string? UpdatedAt, string? Link);
public sealed record VercelDeploymentPage(IReadOnlyList<VercelDeployment>? Deployments, string? PaginationNext);
public sealed record VercelDeployment(string Uid, string? Name, string? Url, string? State, string? ReadyState,
    string? Target, long? CreatedAt, long? BuildingAt, long? Ready);
