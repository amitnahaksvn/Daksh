using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Thin JSON HTTP helper mirroring tests/manual/e2e_scenario.py's `call`/`expect` functions, so the C# tests
/// read as a direct translation of the Python scripts they replace. Returns the raw status code and the
/// parsed JSON body as a <see cref="JsonElement"/> so assertions can read whichever fields they need, the
/// same way the Python scripts index into a dict.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    public async Task<(int Status, JsonElement Body)> CallAsync(HttpMethod method, string path, string? token = null, string? workspaceId = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workspaceId is not null) request.Headers.Add("X-Workspace-Id", workspaceId);
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return ((int)response.StatusCode, json);
    }

    public static JsonElement Expect(string step, int status, JsonElement body, params int[] ok)
    {
        var allowed = ok.Length == 0 ? [200, 201, 202] : ok;
        if (!allowed.Contains(status))
            throw new Xunit.Sdk.XunitException($"[FAIL] {step}: HTTP {status}\n{body}");
        return body;
    }
}

public static class JsonElementExtensions
{
    public static string GetStringProp(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
    public static Guid GetGuidProp(this JsonElement e, string name) => e.GetProperty(name).GetGuid();
    public static string? GetStringOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
}
