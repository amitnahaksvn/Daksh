using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Daksh.Platform.Providers;

internal static class ProviderHttp
{
    public static HttpRequestMessage Json(HttpMethod method, string url, object? body, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var req = new HttpRequestMessage(method, url);
        foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
            req.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, bool stream, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorKind.Timeout, "The provider did not answer in time.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderErrorKind.Unavailable, $"The provider could not be reached: {ex.Message}", inner: ex);
        }
        if (response.IsSuccessStatusCode) return response;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { body = ""; }
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta
                         ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        response.Dispose();
        var detail = ExtractError(body);
        var lower = (detail + " " + body).ToLowerInvariant();
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorKind.Auth,
            HttpStatusCode.PaymentRequired => ProviderErrorKind.Billing,
            HttpStatusCode.TooManyRequests => lower.Contains("quota") || lower.Contains("billing") ? ProviderErrorKind.Billing : ProviderErrorKind.RateLimit,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ProviderErrorKind.Timeout,
            HttpStatusCode.RequestEntityTooLarge => ProviderErrorKind.ContextLength,
            _ when status >= 500 => ProviderErrorKind.Unavailable,
            _ when lower.Contains("context length") || lower.Contains("context_length") || lower.Contains("too many tokens") || lower.Contains("prompt is too long") => ProviderErrorKind.ContextLength,
            _ when status >= 400 => ProviderErrorKind.BadRequest,
            _ => ProviderErrorKind.Unknown
        };
        throw new ProviderException(kind, $"Provider returned {status}: {Truncate(detail, 500)}", status, retryAfter);
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response)";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString()!;
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m)) return m.ToString();
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg)) return msg.ToString();
        }
        catch (JsonException) { }
        return body;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Server-sent events: yields (event name, data) for each event; multi-line data is joined with newlines.</summary>
    public static async IAsyncEnumerable<(string Event, string Data)> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string evt = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (evt, data.ToString());
                evt = "message"; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) evt = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
        if (data.Length > 0) yield return (evt, data.ToString());
    }

    public static async IAsyncEnumerable<string> ReadLinesAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Length > 0) yield return line;
    }

    public static string Join(string? baseUrl, string fallback, string path) =>
        (string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl).TrimEnd('/') + path;
}

/// <summary>Collects streamed tool-call fragments by index into complete calls.</summary>
internal sealed class ToolCallAccumulator
{
    private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Args)> _calls = [];

    /// <summary>Returns true the first time an index gets a name, so the caller can announce the call.</summary>
    public bool Add(int index, string? id, string? name, string? argumentsDelta)
    {
        var isNew = false;
        if (!_calls.TryGetValue(index, out var c)) { c = ("", "", new StringBuilder()); isNew = true; }
        if (!string.IsNullOrEmpty(id)) c.Id = id;
        var named = string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(name);
        if (!string.IsNullOrEmpty(name)) c.Name = name;
        if (!string.IsNullOrEmpty(argumentsDelta)) c.Args.Append(argumentsDelta);
        _calls[index] = c;
        return named || (isNew && !string.IsNullOrEmpty(c.Name));
    }

    public (string Id, string Name) Peek(int index) => (_calls[index].Id, _calls[index].Name);

    public IReadOnlyList<ToolCall> Build() => _calls.Values
        .Where(c => !string.IsNullOrEmpty(c.Name))
        .Select((c, i) => new ToolCall(
            string.IsNullOrEmpty(c.Id) ? $"call_{Guid.NewGuid():N}"[..17] : c.Id,
            c.Name,
            c.Args.Length == 0 ? "{}" : c.Args.ToString()))
        .ToList();
}
