using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Automation;

/// <summary>n8n public REST API client. Requires API access to be enabled on the target instance.</summary>
public sealed class N8nIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<N8nConnection> ProbeAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var request = Request(baseUrl, apiKey, HttpMethod.Get, "api/v1/workflows?limit=1");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return new(true);
    }

    public async Task<N8nWorkflowPage> ListWorkflowsAsync(string baseUrl, string apiKey,
        int limit = 100, string? cursor = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 250);
        var query = $"limit={limit}" + (!string.IsNullOrWhiteSpace(cursor) ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
        using var request = Request(baseUrl, apiKey, HttpMethod.Get, "api/v1/workflows?" + query);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nWorkflowPage>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty workflow list.");
    }

    public async Task<N8nWorkflow> GetWorkflowAsync(string baseUrl, string apiKey, string workflowId,
        CancellationToken ct = default)
    {
        ValidateId(workflowId, nameof(workflowId));
        using var request = Request(baseUrl, apiKey, HttpMethod.Get, $"api/v1/workflows/{Uri.EscapeDataString(workflowId)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nWorkflow>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty workflow.");
    }

    public async Task<N8nWorkflow> CreateWorkflowAsync(string baseUrl, string apiKey, JsonElement workflow,
        CancellationToken ct = default)
    {
        ValidateWorkflow(workflow);
        using var request = Request(baseUrl, apiKey, HttpMethod.Post, "api/v1/workflows");
        request.Content = JsonContent.Create(workflow, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nWorkflow>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty workflow.");
    }

    public async Task<N8nWorkflow> UpdateWorkflowAsync(string baseUrl, string apiKey, string workflowId,
        JsonElement workflow, CancellationToken ct = default)
    {
        ValidateId(workflowId, nameof(workflowId));
        ValidateWorkflow(workflow);
        using var request = Request(baseUrl, apiKey, HttpMethod.Put, $"api/v1/workflows/{Uri.EscapeDataString(workflowId)}");
        request.Content = JsonContent.Create(workflow, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nWorkflow>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty workflow.");
    }

    public async Task<N8nWorkflow> ActivateWorkflowAsync(string baseUrl, string apiKey, string workflowId,
        bool activate = true, CancellationToken ct = default)
    {
        ValidateId(workflowId, nameof(workflowId));
        using var request = Request(baseUrl, apiKey, HttpMethod.Post,
            $"api/v1/workflows/{Uri.EscapeDataString(workflowId)}/{(activate ? "activate" : "deactivate")}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nWorkflow>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty workflow.");
    }

    public async Task<N8nExecutionPage> ListExecutionsAsync(string baseUrl, string apiKey,
        string? workflowId = null, bool? includeData = false, int limit = 100, string? cursor = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 250);
        var query = new List<string> { $"limit={limit}", $"includeData={includeData?.ToString().ToLowerInvariant() ?? "false"}" };
        if (!string.IsNullOrWhiteSpace(workflowId)) query.Add("workflowId=" + Uri.EscapeDataString(workflowId));
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        using var request = Request(baseUrl, apiKey, HttpMethod.Get, "api/v1/executions?" + string.Join("&", query));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nExecutionPage>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty executions response.");
    }

    public async Task<N8nExecution> GetExecutionAsync(string baseUrl, string apiKey, string executionId,
        bool includeData = false, CancellationToken ct = default)
    {
        ValidateId(executionId, nameof(executionId));
        using var request = Request(baseUrl, apiKey, HttpMethod.Get,
            $"api/v1/executions/{Uri.EscapeDataString(executionId)}?includeData={includeData.ToString().ToLowerInvariant()}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<N8nExecution>(Json, ct)
            ?? throw new InvalidOperationException("n8n returned an empty execution.");
    }

    private static HttpRequestMessage Request(string baseUrl, string apiKey, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("n8n API key is required.", nameof(apiKey));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("n8n base URL must be an absolute HTTPS URL without query or fragment.", nameof(baseUrl));
        var normalized = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(normalized, path));
        request.Headers.TryAddWithoutValidation("X-N8N-API-KEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid n8n identifier is required.", name);
    }

    private static void ValidateWorkflow(JsonElement workflow)
    {
        if (workflow.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workflow definition must be a JSON object.", nameof(workflow));
        if (!workflow.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()))
            throw new ArgumentException("Workflow definition must include a non-empty name.", nameof(workflow));
        if (!workflow.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Workflow definition must include a nodes array.", nameof(workflow));
        if (!workflow.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workflow definition must include a connections object.", nameof(workflow));
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"n8n returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record N8nConnection(bool Connected);
public sealed record N8nWorkflowPage(IReadOnlyList<N8nWorkflow>? Data, string? NextCursor);
public sealed record N8nWorkflow(string Id, string? Name, bool Active, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);
public sealed record N8nExecutionPage(IReadOnlyList<N8nExecution>? Data, string? NextCursor);
public sealed record N8nExecution(string Id, string? WorkflowId, string? Status, bool Finished,
    DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, JsonElement? Data);
