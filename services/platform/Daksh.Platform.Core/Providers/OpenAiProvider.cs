using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Providers;

/// <summary>
/// OpenAI Chat Completions wire format. Also serves every endpoint that copies it (Azure OpenAI v1, OpenRouter, Groq,
/// Mistral, vLLM, LM Studio, llama.cpp server, LiteLLM) through the "openai-compatible" kind and a custom base URL.
/// </summary>
public sealed class OpenAiProvider(IHttpClientFactory httpFactory) : IModelProvider
{
    public const string HttpClientName = "model-providers";
    private const string DefaultBase = "https://api.openai.com/v1";

    public IReadOnlyList<string> Kinds => ["openai", "openai-compatible"];

    private static IEnumerable<KeyValuePair<string, string>> Headers(ProviderEndpoint e)
    {
        if (!string.IsNullOrEmpty(e.ApiKey))
            yield return new(e.Options.GetValueOrDefault("authHeader", "Authorization"),
                e.Options.GetValueOrDefault("authPrefix", "Bearer ") + e.ApiKey);
        foreach (var h in e.Headers) yield return h;
    }

    internal static JsonObject BuildBody(ProviderEndpoint endpoint, ChatRequest request, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant() };
            if (m.Parts is { Count: > 0 } && m.Role == ChatRole.User)
            {
                var content = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content)) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var p in m.Parts.Where(p => p.Type == "image"))
                    content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{p.MediaType};base64,{p.Data}" } });
                o["content"] = content;
            }
            else o["content"] = m.Content ?? (m.ToolCalls is { Count: > 0 } ? null : "");
            if (m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 })
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments }
                }).ToArray());
            if (m.Role == ChatRole.Tool) o["tool_call_id"] = m.ToolCallId;
            messages.Add(o);
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = stream };
        if (stream && endpoint.Options.GetValueOrDefault("streamUsage", "true") == "true")
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (request.Tools is { Count: > 0 })
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.Schema.GetRawText()) }
            }).ToArray());
        if (request.Temperature is { } temp) body["temperature"] = temp;
        if (request.MaxOutputTokens is { } max)
            body[endpoint.Options.GetValueOrDefault("maxTokensParam", endpoint.Kind == "openai" ? "max_completion_tokens" : "max_tokens")] = max;
        if (request.ResponseSchema is { } schema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = JsonNode.Parse(schema.GetRawText()) }
            };
        foreach (var (k, v) in request.Options ?? new Dictionary<string, string>())
            if (k.StartsWith("body.", StringComparison.Ordinal))
                body[k[5..]] = TryParse(v);
        return body;
    }

    private static JsonNode? TryParse(string v)
    {
        try { return JsonNode.Parse(v); } catch (JsonException) { return v; }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderEndpoint endpoint, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var url = ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/chat/completions");
        using var req = ProviderHttp.Json(HttpMethod.Post, url, BuildBody(endpoint, request, stream: true).ToJsonString(), Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: true, ct);

        var text = new StringBuilder();
        var calls = new ToolCallAccumulator();
        string finish = "stop", model = request.Model;
        Usage? usage = null;

        await foreach (var (_, data) in ProviderHttp.ReadSseAsync(response, ct))
        {
            if (data == "[DONE]") break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new ProviderException(ProviderErrorKind.Unavailable, "Provider stream error: " + err);
                if (root.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String) model = mo.GetString()!;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage = new Usage(u.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0,
                                      u.TryGetProperty("completion_tokens", out var ctk) ? ctk.GetInt64() : 0);
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString()!;
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } piece)
                {
                    text.Append(piece);
                    yield return new TextDelta(piece);
                }
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        string? id = tc.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString() : null;
                        string? name = null, args = null;
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) args = a.GetString();
                        }
                        if (calls.Add(index, id, name, args))
                        {
                            var (cid, cname) = calls.Peek(index);
                            yield return new ToolCallStarted(index, cid, cname);
                        }
                    }
            }
        }

        var built = calls.Build();
        usage ??= new Usage(TokenEstimator.Estimate(request.Messages) + TokenEstimator.Estimate(request.Tools),
                            TokenEstimator.Estimate(text.ToString()) + built.Sum(b => TokenEstimator.Estimate(b.Arguments)), Estimated: true);
        yield return new ChatCompleted(new ChatResponse(text.ToString(), built, usage, built.Count > 0 ? "tool_calls" : finish, model));
    }

    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Get, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/models"), null, Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: false, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    list.Add(new DiscoveredModel(s, s, Capabilities: Guess(s)));
        return list;
    }

    /// <summary>Capabilities are not reported by the API; this is a conservative guess the operator can correct.</summary>
    internal static ModelCapabilities Guess(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("embed")) return new ModelCapabilities { Chat = false, Streaming = false, Embeddings = true };
        if (l.Contains("whisper") || l.Contains("transcribe")) return new ModelCapabilities { Chat = false, Streaming = false, AudioInput = true };
        if (l.Contains("tts")) return new ModelCapabilities { Chat = false, Streaming = false, AudioOutput = true };
        if (l.Contains("dall-e") || l.Contains("gpt-image")) return new ModelCapabilities { Chat = false, Streaming = false, ImageGeneration = true };
        var gpt = l.StartsWith("gpt-") || l.StartsWith("o1") || l.StartsWith("o3") || l.StartsWith("o4") || l.StartsWith("chatgpt");
        return new ModelCapabilities { Tools = gpt, Vision = gpt && !l.Contains("3.5"), StructuredOutput = gpt && !l.Contains("3.5") };
    }

    public async Task<ProviderHealth> CheckAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try { await DiscoverAsync(endpoint, ct); return new ProviderHealth(true, null, sw.ElapsedMilliseconds); }
        catch (ProviderException ex) { return new ProviderHealth(false, ex.Message, sw.ElapsedMilliseconds); }
    }

    public async Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(ProviderEndpoint endpoint, string model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Post, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/embeddings"),
            new { model, input = inputs }, Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: false, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var vectors = doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
            .Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
        if (vectors.Count != inputs.Count)
            throw new ProviderException(ProviderErrorKind.Unknown, $"Expected {inputs.Count} embeddings, got {vectors.Count}.");
        long tokens = doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : inputs.Sum(TokenEstimator.Estimate);
        return (vectors, new Usage(tokens, 0));
    }
}
