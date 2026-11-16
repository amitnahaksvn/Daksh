using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.ProjectManagement;

/// <summary>Asana REST API client. Supply a user-authorized OAuth access token per call.</summary>
public sealed class AsanaIntegration(HttpClient http)
{
    private const string Base = "https://app.asana.com/api/1.0/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AsanaConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "users/me?opt_fields=gid,name,email");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<AsanaEnvelope<AsanaUser>>(Json, ct);
        var user = result?.Data ?? throw new InvalidOperationException("Asana returned no user.");
        return new(true, user.Gid, user.Name, user.Email);
    }

    public async Task<IReadOnlyList<AsanaTask>> ListTasksAsync(string token, string workspaceGid, string? projectGid = null,
        string? assignee = "me", int limit = 100, string? offset = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceGid)) throw new ArgumentException("Asana workspace GID is required.", nameof(workspaceGid));
        limit = Math.Clamp(limit, 1, 100);
        var query = $"tasks?workspace={Uri.EscapeDataString(workspaceGid)}&assignee={Uri.EscapeDataString(assignee ?? "me")}&limit={limit}&opt_fields=gid,name,notes,completed,due_on,permalink_url";
        if (!string.IsNullOrWhiteSpace(projectGid)) query += $"&project={Uri.EscapeDataString(projectGid)}";
        if (!string.IsNullOrWhiteSpace(offset)) query += $"&offset={Uri.EscapeDataString(offset)}";
        using var request = Request(token, HttpMethod.Get, query);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<AsanaEnvelope<List<AsanaTask>>>(Json, ct);
        return result?.Data ?? [];
    }

    public async Task<AsanaTask> CreateTaskAsync(string token, string workspaceGid, string name, string? notes = null,
        string? projectGid = null, string? assigneeGid = null, DateOnly? dueOn = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceGid)) throw new ArgumentException("Asana workspace GID is required.", nameof(workspaceGid));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Task name is required.", nameof(name));
        var data = new Dictionary<string, object?> { ["workspace"] = workspaceGid, ["name"] = name };
        if (!string.IsNullOrWhiteSpace(notes)) data["notes"] = notes;
        if (!string.IsNullOrWhiteSpace(projectGid)) data["projects"] = new[] { projectGid };
        if (!string.IsNullOrWhiteSpace(assigneeGid)) data["assignee"] = assigneeGid;
        if (dueOn.HasValue) data["due_on"] = dueOn.Value.ToString("yyyy-MM-dd");
        using var request = Request(token, HttpMethod.Post, "tasks");
        request.Content = JsonContent.Create(new { data }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<AsanaEnvelope<AsanaTask>>(Json, ct);
        return result?.Data ?? throw new InvalidOperationException("Asana returned no task.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Asana access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Asana returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record AsanaEnvelope<T>(T? Data);
public sealed record AsanaConnection(bool Connected, string? UserGid = null, string? Name = null, string? Email = null);
public sealed record AsanaUser(string? Gid, string? Name, string? Email);
public sealed record AsanaTask(string Gid, string? Name, string? Notes, bool Completed, string? DueOn, string? PermalinkUrl);
