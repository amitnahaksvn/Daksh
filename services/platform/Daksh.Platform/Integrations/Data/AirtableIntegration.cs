using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Data;

/// <summary>Airtable Web API client. A personal access token is supplied per call.</summary>
public sealed class AirtableIntegration(HttpClient http)
{
    private const string ApiBase = "https://api.airtable.com/v0/";
    private const string MetaBase = "https://api.airtable.com/v0/meta/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AirtableConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = CreateRequest(token, HttpMethod.Get, new Uri(MetaBase + "whoami"));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<AirtableWhoAmI>(Json, ct);
        return new(true, result?.Id, result?.Scopes ?? []);
    }

    public async Task<AirtablePage<AirtableBase>> ListBasesAsync(
        string token, string? offset = null, CancellationToken ct = default)
    {
        var uri = new Uri(MetaBase + "bases" +
            (string.IsNullOrWhiteSpace(offset) ? "" : "?offset=" + Uri.EscapeDataString(offset)));
        using var request = CreateRequest(token, HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var page = await response.Content.ReadFromJsonAsync<AirtableBasePage>(Json, ct);
        return new AirtablePage<AirtableBase>(page?.Bases, page?.Offset);
    }

    public async Task<AirtablePage<AirtableRecord>> ListRecordsAsync(
        string token, string baseId, string tableIdOrName, int pageSize = 100,
        string? offset = null, string? filterByFormula = null, string? view = null,
        CancellationToken ct = default)
    {
        ValidateId(baseId, nameof(baseId));
        ValidateId(tableIdOrName, nameof(tableIdOrName));
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = new List<string> { $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(offset)) query.Add("offset=" + Uri.EscapeDataString(offset));
        if (!string.IsNullOrWhiteSpace(filterByFormula)) query.Add("filterByFormula=" + Uri.EscapeDataString(filterByFormula));
        if (!string.IsNullOrWhiteSpace(view)) query.Add("view=" + Uri.EscapeDataString(view));
        var uri = new Uri(ApiBase + Uri.EscapeDataString(baseId) + "/" +
            Uri.EscapeDataString(tableIdOrName) + "?" + string.Join("&", query));
        using var request = CreateRequest(token, HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var page = await response.Content.ReadFromJsonAsync<AirtableRecordPage>(Json, ct);
        return new AirtablePage<AirtableRecord>(page?.Records, page?.Offset);
    }

    public async Task<AirtableRecord> CreateRecordAsync(
        string token, string baseId, string tableIdOrName, IReadOnlyDictionary<string, object?> fields,
        CancellationToken ct = default)
    {
        ValidateTable(baseId, tableIdOrName);
        ArgumentNullException.ThrowIfNull(fields);
        using var request = CreateRequest(token, HttpMethod.Post,
            new Uri(ApiBase + Uri.EscapeDataString(baseId) + "/" + Uri.EscapeDataString(tableIdOrName)));
        request.Content = JsonContent.Create(new { fields }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<AirtableRecord>(Json, ct)
            ?? throw new InvalidOperationException("Airtable returned an empty record response.");
    }

    public async Task<AirtableRecord> UpdateRecordAsync(
        string token, string baseId, string tableIdOrName, string recordId,
        IReadOnlyDictionary<string, object?> fields, bool replace = false, CancellationToken ct = default)
    {
        ValidateTable(baseId, tableIdOrName);
        ValidateId(recordId, nameof(recordId));
        ArgumentNullException.ThrowIfNull(fields);
        using var request = CreateRequest(token, replace ? HttpMethod.Put : HttpMethod.Patch,
            new Uri(ApiBase + Uri.EscapeDataString(baseId) + "/" +
                Uri.EscapeDataString(tableIdOrName) + "/" + Uri.EscapeDataString(recordId)));
        request.Content = JsonContent.Create(new { fields }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<AirtableRecord>(Json, ct)
            ?? throw new InvalidOperationException("Airtable returned an empty record response.");
    }

    public async Task DeleteRecordAsync(
        string token, string baseId, string tableIdOrName, string recordId, CancellationToken ct = default)
    {
        ValidateTable(baseId, tableIdOrName);
        ValidateId(recordId, nameof(recordId));
        using var request = CreateRequest(token, HttpMethod.Delete,
            new Uri(ApiBase + Uri.EscapeDataString(baseId) + "/" +
                Uri.EscapeDataString(tableIdOrName) + "/" + Uri.EscapeDataString(recordId)));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    private static HttpRequestMessage CreateRequest(string token, HttpMethod method, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Airtable personal access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateTable(string baseId, string table)
    {
        ValidateId(baseId, nameof(baseId));
        ValidateId(table, nameof(table));
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Airtable identifier or table name is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Airtable returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body[..Math.Min(body.Length, 500)]}",
                null, response.StatusCode);
        }
    }
}

public sealed record AirtableConnection(bool Connected, string? UserId, IReadOnlyList<string> Scopes);
public sealed record AirtableWhoAmI(string? Id, IReadOnlyList<string>? Scopes);
public sealed record AirtablePage<T>(IReadOnlyList<T>? Items, string? Offset)
{
    public IReadOnlyList<AirtableBase>? Bases => Items as IReadOnlyList<AirtableBase>;
    public IReadOnlyList<AirtableRecord>? Records => Items as IReadOnlyList<AirtableRecord>;
}
public sealed record AirtableBasePage(
    [property: JsonPropertyName("bases")] IReadOnlyList<AirtableBase>? Bases,
    [property: JsonPropertyName("offset")] string? Offset);
public sealed record AirtableRecordPage(
    [property: JsonPropertyName("records")] IReadOnlyList<AirtableRecord>? Records,
    [property: JsonPropertyName("offset")] string? Offset);
public sealed record AirtableBase(string Id, string Name, string PermissionLevel);
public sealed record AirtableRecord(
    string Id,
    [property: JsonPropertyName("createdTime")] DateTimeOffset? CreatedTime,
    JsonElement Fields);
