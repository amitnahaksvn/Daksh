using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Cloud;

/// <summary>Supabase Management API client. A personal access token is supplied per call.</summary>
public sealed class SupabaseIntegration(HttpClient http)
{
    private const string Base = "https://api.supabase.com/v1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SupabaseOrganization>> ListOrganizationsAsync(string token,
        CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "organizations");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SupabaseOrganization>>(Json, ct) ?? [];
    }

    public async Task<IReadOnlyList<SupabaseProject>> ListProjectsAsync(string token, string? organizationId = null,
        CancellationToken ct = default)
    {
        var path = "projects";
        if (!string.IsNullOrWhiteSpace(organizationId)) path += "?organization_id=" + Uri.EscapeDataString(organizationId);
        using var request = Request(token, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SupabaseProject>>(Json, ct) ?? [];
    }

    public async Task<SupabaseProject> GetProjectAsync(string token, string projectRef, CancellationToken ct = default)
    {
        ValidateId(projectRef, nameof(projectRef));
        using var request = Request(token, HttpMethod.Get, $"projects/{Uri.EscapeDataString(projectRef)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<SupabaseProject>(Json, ct)
            ?? throw new InvalidOperationException("Supabase returned an empty project.");
    }

    public async Task<SupabaseProject> CreateProjectAsync(string token, SupabaseCreateProject draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.OrganizationId)
            || string.IsNullOrWhiteSpace(draft.DatabasePassword) || string.IsNullOrWhiteSpace(draft.Region))
            throw new ArgumentException("Project name, organization, database password and region are required.", nameof(draft));
        using var request = Request(token, HttpMethod.Post, "projects");
        request.Content = JsonContent.Create(new
        {
            name = draft.Name,
            organization_id = draft.OrganizationId,
            db_pass = draft.DatabasePassword,
            region = draft.Region,
            plan = draft.Plan ?? "free"
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<SupabaseProject>(Json, ct)
            ?? throw new InvalidOperationException("Supabase returned an empty project creation response.");
    }

    public async Task DeleteProjectAsync(string token, string projectRef, CancellationToken ct = default)
    {
        ValidateId(projectRef, nameof(projectRef));
        using var request = Request(token, HttpMethod.Delete, $"projects/{Uri.EscapeDataString(projectRef)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Supabase personal access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Supabase project reference is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Supabase returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record SupabaseOrganization(string Id, string? Name, string? Slug);
public sealed record SupabaseProject(string Id, string Name,
    [property: JsonPropertyName("organization_id")] string? OrganizationId,
    string? Region, string? Status,
    [property: JsonPropertyName("database_version")] string? DatabaseVersion,
    [property: JsonPropertyName("created_at")] string? CreatedAt, string? Ref,
    [property: JsonPropertyName("project_url")] string? Url);
public sealed record SupabaseCreateProject(string Name, string OrganizationId, string DatabasePassword,
    string Region, string? Plan = "free");
