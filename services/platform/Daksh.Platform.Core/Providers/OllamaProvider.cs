using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Providers;

/// <summary>Ollama native API (/api/chat, /api/embed, /api/tags).</summary>
public sealed class OllamaProvider(IHttpClientFactory httpFactory) : IModelProvider
{
    private const string DefaultBase = "http://localhost:11434";

    public IReadOnlyList<string> Kinds => ["ollama"];

    private static IEnumerable<KeyValuePair<string, string>> Headers(ProviderEndpoint e)
    {
        if (!string.IsNullOrEmpty(e.ApiKey)) yield return new("Authorization", "Bearer " + e.ApiKey);
        foreach (var h in e.Headers) yield return h;
    }

    internal static JsonObject BuildBody(ChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content ?? "" };
            if (m.Parts?.Where(p => p.Type == "image").ToList() is { Count: > 0 } images)
                o["images"] = new JsonArray(images.Select(p => (JsonNode)p.Data).ToArray());
            if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(t => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = SafeParse(t.Arguments) }
                }).ToArray());
            if (m.Role == ChatRole.Tool && m.ToolName is not null) o["tool_name"] = m.ToolName;
            messages.Add(o);
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = stream };
        var options = new JsonObject();
        if (request.Temperature is { } t) options["temperature"] = t;
        if (request.MaxOutputTokens is { } max) options["num_predict"] = max;
        if (options.Count > 0) body["options"] = options;
        if (request.Tools is { Count: > 0 })
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = JsonNode.Parse(tool.Schema.GetRawText()) }
            }).ToArray());
        if (request.ResponseSchema is { } schema) body["format"] = JsonNode.Parse(schema.GetRawText());
        return body;
    }

    private static JsonNode SafeParse(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); } catch (JsonException) { return new JsonObject(); }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderEndpoint endpoint, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiProvider.HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Post, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/api/chat"),
            BuildBody(request, stream: true).ToJsonString(), Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: true, ct);

        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        long input = 0, output = 0;
        var finish = "stop";

        await foreach (var line in ProviderHttp.ReadLinesAsync(response, ct))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new ProviderException(ProviderErrorKind.Unavailable, "Ollama error: " + err);
                if (root.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } piece)
                    {
                        text.Append(piece);
                        yield return new TextDelta(piece);
                    }
                    if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            if (!tc.TryGetProperty("function", out var fn)) continue;
                            var call = new ToolCall($"call_{Guid.NewGuid():N}"[..17], fn.GetProperty("name").GetString() ?? "",
                                fn.TryGetProperty("arguments", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()) : "{}");
                            calls.Add(call);
                            yield return new ToolCallStarted(calls.Count - 1, call.Id, call.Name);
                        }
                }
                if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                {
                    if (root.TryGetProperty("prompt_eval_count", out var pe)) input = pe.GetInt64();
                    if (root.TryGetProperty("eval_count", out var ec)) output = ec.GetInt64();
                    if (root.TryGetProperty("done_reason", out var dr) && dr.GetString() is { } r) finish = r;
                }
            }
        }

        var usage = input + output > 0
            ? new Usage(input, output)
            : new Usage(TokenEstimator.Estimate(request.Messages), TokenEstimator.Estimate(text.ToString()), Estimated: true);
        yield return new ChatCompleted(new ChatResponse(text.ToString(), calls, usage, calls.Count > 0 ? "tool_calls" : finish, request.Model));
    }

    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiProvider.HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Get, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/api/tags"), null, Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: false, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var m in models.EnumerateArray())
                if (m.TryGetProperty("name", out var n) && n.GetString() is { } name)
                {
                    var embed = name.Contains("embed", StringComparison.OrdinalIgnoreCase);
                    // Tool and vision support differ per model and are not listed by /api/tags; left off until the operator sets them.
                    list.Add(new DiscoveredModel(name, name, null, embed
                        ? new ModelCapabilities { Chat = false, Streaming = false, Embeddings = true }
                        : new ModelCapabilities()));
                }
        return list;
    }

    public async Task<ProviderHealth> CheckAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try { await DiscoverAsync(endpoint, ct); return new ProviderHealth(true, null, sw.ElapsedMilliseconds); }
        catch (ProviderException ex) { return new ProviderHealth(false, ex.Message, sw.ElapsedMilliseconds); }
    }

    public async Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(ProviderEndpoint endpoint, string model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiProvider.HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Post, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/api/embed"), new { model, input = inputs }, Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: false, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var vectors = doc.RootElement.GetProperty("embeddings").EnumerateArray()
            .Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
        if (vectors.Count != inputs.Count)
            throw new ProviderException(ProviderErrorKind.Unknown, $"Expected {inputs.Count} embeddings, got {vectors.Count}.");
        long tokens = doc.RootElement.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt64() : inputs.Sum(TokenEstimator.Estimate);
        return (vectors, new Usage(tokens, 0));
    }
}
