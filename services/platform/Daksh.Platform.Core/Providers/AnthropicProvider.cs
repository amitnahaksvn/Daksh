using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Providers;

/// <summary>Anthropic Messages wire format; "anthropic-compatible" is the same format on a custom base URL.</summary>
public sealed class AnthropicProvider(IHttpClientFactory httpFactory) : IModelProvider
{
    private const string DefaultBase = "https://api.anthropic.com";

    public IReadOnlyList<string> Kinds => ["anthropic", "anthropic-compatible"];

    private static IEnumerable<KeyValuePair<string, string>> Headers(ProviderEndpoint e)
    {
        if (!string.IsNullOrEmpty(e.ApiKey)) yield return new("x-api-key", e.ApiKey);
        yield return new("anthropic-version", e.Options.GetValueOrDefault("apiVersion", "2023-06-01"));
        foreach (var h in e.Headers) yield return h;
    }

    internal static JsonObject BuildBody(ChatRequest request, bool stream)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var messages = new JsonArray();
        JsonObject? last = null;

        void Append(string role, JsonNode block)
        {
            // The API requires alternating roles: consecutive blocks of one role are merged into one message.
            if (last is not null && last["role"]!.GetValue<string>() == role) { ((JsonArray)last["content"]!).Add(block); return; }
            last = new JsonObject { ["role"] = role, ["content"] = new JsonArray(block) };
            messages.Add(last);
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.System: break;
                case ChatRole.User:
                    foreach (var p in m.Parts?.Where(p => p.Type == "image") ?? [])
                        Append("user", new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = p.MediaType, ["data"] = p.Data }
                        });
                    if (!string.IsNullOrEmpty(m.Content) || m.Parts is not { Count: > 0 })
                        Append("user", new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(m.Content) ? "(empty)" : m.Content });
                    break;
                case ChatRole.Assistant:
                    if (!string.IsNullOrEmpty(m.Content)) Append("assistant", new JsonObject { ["type"] = "text", ["text"] = m.Content });
                    foreach (var t in m.ToolCalls ?? [])
                        Append("assistant", new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = t.Id, ["name"] = t.Name,
                            ["input"] = ParseObject(t.Arguments)
                        });
                    break;
                case ChatRole.Tool:
                    Append("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content ?? "" });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens ?? 4096,
            ["messages"] = messages,
            ["stream"] = stream
        };
        if (system.Length > 0) body["system"] = system;
        if (request.Temperature is { } temp) body["temperature"] = temp;
        if (request.Tools is { Count: > 0 })
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.Schema.GetRawText())
            }).ToArray());
        return body;
    }

    private static JsonNode ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderEndpoint endpoint, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiProvider.HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Post, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/v1/messages"),
            BuildBody(request, stream: true).ToJsonString(), Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: true, ct);

        var text = new StringBuilder();
        var calls = new ToolCallAccumulator();
        long input = 0, output = 0;
        string finish = "stop", model = request.Model;

        await foreach (var (evt, data) in ProviderHttp.ReadSseAsync(response, ct))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : evt;
                switch (type)
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var msg))
                        {
                            if (msg.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String) model = mo.GetString()!;
                            if (msg.TryGetProperty("usage", out var u0) && u0.TryGetProperty("input_tokens", out var it)) input = it.GetInt64();
                        }
                        break;
                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var block) && block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
                        {
                            var index = root.GetProperty("index").GetInt32();
                            calls.Add(index, block.GetProperty("id").GetString(), block.GetProperty("name").GetString(), null);
                            var (cid, cname) = calls.Peek(index);
                            yield return new ToolCallStarted(index, cid, cname);
                        }
                        break;
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var dt))
                        {
                            if (dt.GetString() == "text_delta" && delta.GetProperty("text").GetString() is { Length: > 0 } piece)
                            {
                                text.Append(piece);
                                yield return new TextDelta(piece);
                            }
                            else if (dt.GetString() == "input_json_delta")
                                calls.Add(root.GetProperty("index").GetInt32(), null, null, delta.GetProperty("partial_json").GetString());
                        }
                        break;
                    case "message_delta":
                        if (root.TryGetProperty("delta", out var md) && md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                            finish = sr.GetString() switch { "tool_use" => "tool_calls", "max_tokens" => "length", "end_turn" => "stop", var other => other ?? "stop" };
                        if (root.TryGetProperty("usage", out var u1) && u1.TryGetProperty("output_tokens", out var ot)) output = ot.GetInt64();
                        break;
                    case "error":
                        var message = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em) ? em.GetString() : data;
                        var overloaded = root.TryGetProperty("error", out var e2) && e2.TryGetProperty("type", out var et) && et.GetString() is "overloaded_error" or "rate_limit_error";
                        throw new ProviderException(overloaded ? ProviderErrorKind.RateLimit : ProviderErrorKind.Unavailable, "Provider stream error: " + message);
                }
            }
        }

        var built = calls.Build();
        var usage = input + output > 0
            ? new Usage(input, output)
            : new Usage(TokenEstimator.Estimate(request.Messages), TokenEstimator.Estimate(text.ToString()), Estimated: true);
        yield return new ChatCompleted(new ChatResponse(text.ToString(), built, usage, built.Count > 0 ? "tool_calls" : finish, model));
    }

    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(OpenAiProvider.HttpClientName);
        using var req = ProviderHttp.Json(HttpMethod.Get, ProviderHttp.Join(endpoint.BaseUrl, DefaultBase, "/v1/models"), null, Headers(endpoint));
        using var response = await ProviderHttp.SendAsync(http, req, stream: false, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<DiscoveredModel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    list.Add(new DiscoveredModel(s, m.TryGetProperty("display_name", out var dn) ? dn.GetString() : s, 200_000,
                        new ModelCapabilities { Tools = true, Vision = true }));
        return list;
    }

    public async Task<ProviderHealth> CheckAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try { await DiscoverAsync(endpoint, ct); return new ProviderHealth(true, null, sw.ElapsedMilliseconds); }
        catch (ProviderException ex) { return new ProviderHealth(false, ex.Message, sw.ElapsedMilliseconds); }
    }

    public Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(ProviderEndpoint endpoint, string model, IReadOnlyList<string> inputs, CancellationToken ct) =>
        throw new CapabilityException("The Anthropic API has no embeddings endpoint. Configure another embedding provider.");
}
