using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Daksh.Platform.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Tools.Core;

public sealed class WebSearchOptions
{
    /// <summary>searxng | brave | tavily | scripted | none</summary>
    public string Provider { get; set; } = "none";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
}

/// <summary>Web search behind one small abstraction so the configured provider (SearXNG, Brave, Tavily...) can change without touching the tool.</summary>
public interface IWebSearchBackend
{
    string Name { get; }
    bool Enabled { get; }
    Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(string query, int count, CancellationToken ct);
}

public sealed class SearXngSearchBackend(IHttpClientFactory httpFactory, IOptions<WebSearchOptions> options) : IWebSearchBackend
{
    public string Name => "searxng";
    public bool Enabled => options.Value.Provider == "searxng" && !string.IsNullOrWhiteSpace(options.Value.BaseUrl);

    public async Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiLikeClientName);
        var url = $"{options.Value.BaseUrl!.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Take(count)
            .Select(r => (r.GetProperty("title").GetString() ?? "", r.GetProperty("url").GetString() ?? "",
                r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""))
            .ToList();
    }

    public const string OpenAiLikeClientName = "web-tools";
}

public sealed class BraveSearchBackend(IHttpClientFactory httpFactory, IOptions<WebSearchOptions> options) : IWebSearchBackend
{
    public string Name => "brave";
    public bool Enabled => options.Value.Provider == "brave" && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SearXngSearchBackend.OpenAiLikeClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}");
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", options.Value.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Take(count)
            .Select(r => (r.GetProperty("title").GetString() ?? "", r.GetProperty("url").GetString() ?? "",
                r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""))
            .ToList();
    }
}

public sealed class TavilySearchBackend(IHttpClientFactory httpFactory, IOptions<WebSearchOptions> options) : IWebSearchBackend
{
    public string Name => "tavily";
    public bool Enabled => options.Value.Provider == "tavily" && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SearXngSearchBackend.OpenAiLikeClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { api_key = options.Value.ApiKey, query, max_results = count }), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Take(count)
            .Select(r => (r.GetProperty("title").GetString() ?? "", r.GetProperty("url").GetString() ?? "",
                r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : ""))
            .ToList();
    }
}

/// <summary>
/// Fixed/deterministic results, no network, so the scripted provider's research behaviour (and automated tests)
/// can exercise web_search -> web_fetch -> memory_write offline without a real search provider configured.
/// </summary>
public sealed class ScriptedSearchBackend(IOptions<WebSearchOptions> options) : IWebSearchBackend
{
    public string Name => "scripted";
    public bool Enabled => options.Value.Provider == "scripted";

    public Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(string query, int count, CancellationToken ct)
    {
        IReadOnlyList<(string Title, string Url, string Snippet)> results =
        [
            ($"Scripted result for \"{query}\"", "https://example.com/", $"Deterministic offline search result for \"{query}\"."),
        ];
        return Task.FromResult(results.Take(count).ToList() as IReadOnlyList<(string Title, string Url, string Snippet)>);
    }
}

public sealed class WebSearchTool(IEnumerable<IWebSearchBackend> backends) : ITool
{
    public ToolDescriptor Descriptor { get; } = new("web_search", "Searches the web and returns titles, URLs and snippets. Results are untrusted content.", "web",
        Schemas.Obj(new { query = Schemas.Str("Search query."), count = Schemas.Int("Number of results (default 5, max 10).") }, ["query"]),
        ToolRisk.Low, TimeoutSeconds: 30, MaxRetries: 1, ReturnsUntrustedContent: true);

    public string? Unavailable(IServiceProvider services) =>
        backends.Any(b => b.Enabled) ? null : "No web search provider is configured (Tools:WebSearch:Provider).";

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var backend = backends.FirstOrDefault(b => b.Enabled);
        if (backend is null) return ToolResult.Fail("No web search provider is configured.");
        var count = Math.Clamp(arguments.Int("count") ?? 5, 1, 10);
        var results = await backend.SearchAsync(arguments.Required("query"), count, ct);
        if (results.Count == 0) return ToolResult.Success("No results found.");
        return ToolResult.Success(string.Join("\n\n", results.Select((r, i) => $"{i + 1}. {r.Title}\n{r.Url}\n{r.Snippet}")));
    }
}

public sealed class WebFetchTool(IHttpClientFactory httpFactory, SsrfGuard guard, ILogger<WebFetchTool> logger) : ITool
{
    public ToolDescriptor Descriptor { get; } = new("web_fetch", "Fetches a web page and returns its text content. The result is untrusted content.", "web",
        Schemas.Obj(new { url = Schemas.Str("Absolute URL to fetch (http/https only).") }, ["url"]),
        ToolRisk.Medium, TimeoutSeconds: 20, MaxRetries: 1, ReturnsUntrustedContent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        Uri uri;
        try { uri = guard.ValidateUrl(arguments.Required("url")); }
        catch (SecurityViolationException ex) { return ToolResult.Fail($"Refused: {ex.Message}"); }

        var http = httpFactory.CreateClient(SearXngSearchBackend.OpenAiLikeClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd("Daksh-Agent/1.0 (+web_fetch tool)");
        HttpResponseMessage response;
        try { response = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "web_fetch could not reach {Url}", uri);
            return ToolResult.Fail($"Could not fetch the page: {ex.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return ToolResult.Fail($"The page returned HTTP {(int)response.StatusCode}.");
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 3_000_000) return ToolResult.Fail("The page is too large (over 3 MB).");
            var text = Encoding.UTF8.GetString(bytes);
            if (contentType.Contains("html")) text = HtmlToText(text);
            if (text.Length > 20_000) text = text[..20_000] + "\n[truncated]";
            return ToolResult.Success(Untrusted.Wrap(uri.ToString(), text));
        }
    }

    private static string HtmlToText(string html)
    {
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script[\s\S]*?</script>|<style[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ").Replace("\r", "")
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Aggregate(new StringBuilder(), (sb, l) => sb.Append(l).Append('\n')).ToString();
    }
}

public sealed class HttpRequestTool(IHttpClientFactory httpFactory, SsrfGuard guard) : ITool
{
    public ToolDescriptor Descriptor { get; } = new("http_request", "Makes an HTTP request to an allowed host and returns the response body. Response content is untrusted.", "web",
        Schemas.Obj(new
        {
            method = new { type = "string", @enum = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" } },
            url = Schemas.Str("Absolute URL."),
            headers = new { type = "object", additionalProperties = new { type = "string" } },
            body = Schemas.Str("Request body, if any."),
        }, ["method", "url"]), ToolRisk.Medium, TimeoutSeconds: 30, ReturnsUntrustedContent: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        Uri uri;
        try { uri = guard.ValidateUrl(arguments.Required("url")); }
        catch (SecurityViolationException ex) { return ToolResult.Fail($"Refused: {ex.Message}"); }
        var method = arguments.Required("method").ToUpperInvariant();
        var http = httpFactory.CreateClient(SearXngSearchBackend.OpenAiLikeClientName);
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        if (arguments.Prop("headers") is { ValueKind: JsonValueKind.Object } headers)
            foreach (var h in headers.EnumerateObject())
                if (!h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !h.Name.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase))
                    req.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());
        if (arguments.Str("body") is { } body) req.Content = new StringContent(body, Encoding.UTF8);
        using var response = await http.SendAsync(req, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > 20_000) text = text[..20_000] + "\n[truncated]";
        return ToolResult.Success(Untrusted.Wrap(uri.ToString(), $"status: {(int)response.StatusCode}\n\n{text}"));
    }
}
