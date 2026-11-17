using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Cloud;

/// <summary>Cloudflare API client for account/zone discovery and DNS record management.</summary>
public sealed class CloudflareIntegration(HttpClient http)
{
    private const string Base = "https://api.cloudflare.com/client/v4/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CloudflareResultPage<CloudflareZone>> ListZonesAsync(string token, string? name = null,
        int page = 1, int perPage = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 50);
        var query = $"page={page}&per_page={perPage}";
        if (!string.IsNullOrWhiteSpace(name)) query += "&name=" + Uri.EscapeDataString(name);
        using var request = Request(token, HttpMethod.Get, "zones?" + query);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareResultPage<CloudflareZone>>>(Json, ct);
        return RequireSuccess(result);
    }

    public async Task<CloudflareResultPage<CloudflareDnsRecord>> ListDnsRecordsAsync(string token, string zoneId,
        string? name = null, string? type = null, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        ValidateId(zoneId, nameof(zoneId));
        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 100);
        var query = $"page={page}&per_page={perPage}";
        if (!string.IsNullOrWhiteSpace(name)) query += "&name=" + Uri.EscapeDataString(name);
        if (!string.IsNullOrWhiteSpace(type)) query += "&type=" + Uri.EscapeDataString(type.ToUpperInvariant());
        using var request = Request(token, HttpMethod.Get, $"zones/{Uri.EscapeDataString(zoneId)}/dns_records?{query}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareResultPage<CloudflareDnsRecord>>>(Json, ct);
        return RequireSuccess(result);
    }

    public async Task<CloudflareDnsRecord> CreateDnsRecordAsync(string token, string zoneId,
        CloudflareDnsRecordDraft draft, CancellationToken ct = default)
    {
        ValidateId(zoneId, nameof(zoneId));
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Type) || string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Content))
            throw new ArgumentException("DNS record type, name and content are required.", nameof(draft));
        if (draft.Ttl is < 60 or > 86400 && draft.Ttl != 1)
            throw new ArgumentOutOfRangeException(nameof(draft), "TTL must be 1 (automatic) or between 60 and 86400 seconds.");
        using var request = Request(token, HttpMethod.Post, $"zones/{Uri.EscapeDataString(zoneId)}/dns_records");
        request.Content = JsonContent.Create(new
        {
            type = draft.Type.ToUpperInvariant(), name = draft.Name, content = draft.Content,
            ttl = draft.Ttl ?? 1, proxied = draft.Proxied, priority = draft.Priority, comment = draft.Comment
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareDnsRecord>>(Json, ct);
        return RequireSuccess(result);
    }

    public async Task<CloudflareDnsRecord> UpdateDnsRecordAsync(string token, string zoneId, string recordId,
        CloudflareDnsRecordDraft draft, CancellationToken ct = default)
    {
        ValidateId(zoneId, nameof(zoneId));
        ValidateId(recordId, nameof(recordId));
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Type) || string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Content))
            throw new ArgumentException("DNS record type, name and content are required.", nameof(draft));
        using var request = Request(token, HttpMethod.Put,
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}");
        request.Content = JsonContent.Create(new
        {
            type = draft.Type.ToUpperInvariant(), name = draft.Name, content = draft.Content,
            ttl = draft.Ttl ?? 1, proxied = draft.Proxied, priority = draft.Priority, comment = draft.Comment
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<CloudflareDnsRecord>>(Json, ct);
        return RequireSuccess(result);
    }

    public async Task DeleteDnsRecordAsync(string token, string zoneId, string recordId, CancellationToken ct = default)
    {
        ValidateId(zoneId, nameof(zoneId));
        ValidateId(recordId, nameof(recordId));
        using var request = Request(token, HttpMethod.Delete,
            $"zones/{Uri.EscapeDataString(zoneId)}/dns_records/{Uri.EscapeDataString(recordId)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<JsonElement>>(Json, ct);
        if (result is null || !result.Success) throw new InvalidOperationException("Cloudflare did not confirm DNS record deletion.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Cloudflare API token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static T RequireSuccess<T>(CloudflareEnvelope<T>? envelope)
    {
        if (envelope is null || !envelope.Success || envelope.Result is null)
            throw new InvalidOperationException("Cloudflare API reported an unsuccessful operation.");
        return envelope.Result;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Cloudflare identifier is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Cloudflare returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record CloudflareEnvelope<T>(bool Success, T? Result,
    [property: JsonPropertyName("errors")] IReadOnlyList<JsonElement>? Errors = null);
public sealed record CloudflareResultPage<T>(IReadOnlyList<T>? Result, JsonElement? ResultInfo);
public sealed record CloudflareZone(string Id, string Name, string? Status, string? Type, string? AccountId);
public sealed record CloudflareDnsRecord(string Id, string Type, string Name, string Content, int Ttl,
    bool Proxied, int? Priority, string? Comment);
public sealed record CloudflareDnsRecordDraft(string Type, string Name, string Content,
    int? Ttl = 1, bool? Proxied = null, int? Priority = null, string? Comment = null);
