using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Providers;

/// <summary>A named deterministic "model". The model id of a scripted provider selects the behaviour.</summary>
public interface IScriptedBehavior
{
    string Name { get; }
    ChatResponse Respond(ChatRequest request);
}

/// <summary>
/// Deterministic provider with no network access. It exists so the whole platform (tool loop, memory, scheduling,
/// channels) can be exercised offline and in automated tests. It is not an AI model and is labelled as simulated.
/// Model ids: a behaviour name ("echo", "research"), or "fail-auth" / "fail-ratelimit" / "fail-unavailable" to simulate outages.
/// </summary>
public sealed class ScriptedProvider(IEnumerable<IScriptedBehavior> behaviors) : IModelProvider
{
    public const int EmbeddingDimensions = 256;
    private readonly Dictionary<string, IScriptedBehavior> _behaviors = behaviors.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.Last());

    public IReadOnlyList<string> Kinds => ["scripted"];

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderEndpoint endpoint, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        switch (request.Model)
        {
            case "fail-auth": throw new ProviderException(ProviderErrorKind.Auth, "Simulated authentication failure.", 401);
            case "fail-ratelimit": throw new ProviderException(ProviderErrorKind.RateLimit, "Simulated rate limit.", 429, TimeSpan.FromMilliseconds(10));
            case "fail-unavailable": throw new ProviderException(ProviderErrorKind.Unavailable, "Simulated outage.", 503);
        }
        if (!_behaviors.TryGetValue(request.Model, out var behavior))
            throw new ProviderException(ProviderErrorKind.BadRequest, $"Unknown scripted model '{request.Model}'. Known: {string.Join(", ", _behaviors.Keys)}", 404);

        var delay = int.TryParse(endpoint.Options.GetValueOrDefault("delayMs"), out var d) ? d : 0;
        var response = behavior.Respond(request);
        foreach (var chunk in Chunk(response.Text))
        {
            if (delay > 0) await Task.Delay(delay, ct);
            ct.ThrowIfCancellationRequested();
            yield return new TextDelta(chunk);
        }
        for (var i = 0; i < response.ToolCalls.Count; i++)
            yield return new ToolCallStarted(i, response.ToolCalls[i].Id, response.ToolCalls[i].Name);
        yield return new ChatCompleted(response);
    }

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var next = text.IndexOf(' ', Math.Min(i + 1, text.Length));
            var end = next < 0 ? text.Length : next;
            yield return text[i..end];
            i = end;
        }
    }

    public Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ProviderEndpoint endpoint, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DiscoveredModel>>(_behaviors.Keys
            .Select(k => new DiscoveredModel(k, $"Scripted: {k} (simulated)", 16_000, new ModelCapabilities { Tools = true, StructuredOutput = true }))
            .Append(new DiscoveredModel("hash-embedding", "Hash embedding (lexical, simulated)", null,
                new ModelCapabilities { Chat = false, Streaming = false, Embeddings = true }))
            .ToList());

    public Task<ProviderHealth> CheckAsync(ProviderEndpoint endpoint, CancellationToken ct) => Task.FromResult(new ProviderHealth(true));

    public Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(ProviderEndpoint endpoint, string model, IReadOnlyList<string> inputs, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<float[]>, Usage)>((inputs.Select(HashEmbedding.Embed).ToList(), new Usage(inputs.Sum(TokenEstimator.Estimate), 0, true)));

    public static ChatResponse Text(ChatRequest request, string text) =>
        new(text, [], new Usage(TokenEstimator.Estimate(request.Messages), TokenEstimator.Estimate(text), true), "stop", request.Model);

    public static ChatResponse Call(ChatRequest request, string tool, object arguments, string? preface = null)
    {
        var args = JsonSerializer.Serialize(arguments);
        return new(preface ?? "", [new ToolCall("call_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(tool + args + request.Messages.Count)))[..12], tool, args)],
            new Usage(TokenEstimator.Estimate(request.Messages), TokenEstimator.Estimate(args), true), "tool_calls", request.Model);
    }
}

/// <summary>
/// Feature-hashing bag-of-words embedding. Deterministic and dependency-free; it captures shared vocabulary, not meaning.
/// Used by the scripted provider and as the documented offline option.
/// </summary>
public static partial class HashEmbedding
{
    [GeneratedRegex(@"[\p{L}\p{Nd}]+")] private static partial Regex Word();

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the","a","an","and","or","of","to","in","on","for","is","are","was","were","be","it","this","that","with","as","at","by","from",
        "what","which","who","how","do","does","did","you","your","i","me","my","we","our","about","can","could","would","should","have","has"
    };

    public static float[] Embed(string text)
    {
        var v = new float[ScriptedProvider.EmbeddingDimensions];
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 2 || Stop.Contains(w)) continue;
            if (w.Length > 4 && w.EndsWith('s')) w = w[..^1];
            uint h = 2166136261;
            foreach (var c in w) { h ^= c; h *= 16777619; }
            v[h % (uint)v.Length] += (h & 0x80000000) == 0 ? 1f : -1f;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}

/// <summary>Repeats the last user message. Used for smoke tests.</summary>
public sealed class EchoBehavior : IScriptedBehavior
{
    public string Name => "echo";
    public ChatResponse Respond(ChatRequest request)
    {
        var last = request.Messages.LastOrDefault(m => m.Role is ChatRole.User or ChatRole.Tool);
        return ScriptedProvider.Text(request, last?.Role == ChatRole.Tool ? $"Tool result: {last.Content}" : $"You said: {last?.Content}");
    }
}

/// <summary>
/// Rule-based research assistant used by the end-to-end scenario: search the web, fetch the first result, save a
/// summary to memory, answer follow-ups from memory, and create schedules. Every step is a real tool call made
/// through the normal runtime; only the "thinking" is scripted.
/// </summary>
public sealed partial class ResearchBehavior : IScriptedBehavior
{
    public string Name => "research";

    [GeneratedRegex(@"https?://[^\s""'<>)\]]+")] private static partial Regex Url();
    [GeneratedRegex(@"(?i)\b(?:research|search\s+for|look\s+up|find\s+out\s+about|investigate)\b[:\s]+(.+)", RegexOptions.Singleline)] private static partial Regex ResearchIntent();
    [GeneratedRegex(@"(?i)\b(schedule|remind\s+me|every\s+\d+\s+(second|minute|hour)s?|every\s+(day|hour|week)|daily|weekly|tomorrow|in\s+\d+\s+(second|minute|hour)s?)\b")] private static partial Regex ScheduleIntent();
    [GeneratedRegex(@"(?i)\b(remember|recall|what\s+did\s+(you|we|i)|earlier|previously|you\s+found|told\s+you|do\s+you\s+know)\b")] private static partial Regex RecallIntent();
    [GeneratedRegex(@"(?i)\bin\s+(\d+)\s+(second|minute|hour)s?\b")] private static partial Regex InDelay();
    [GeneratedRegex(@"(?i)\bevery\s+(\d+)\s+(second|minute|hour)s?\b")] private static partial Regex EveryN();
    [GeneratedRegex(@"(?i)\b(?:daily|every\s+day)\b(?:.*?\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?")] private static partial Regex Daily();
    [GeneratedRegex(@"<<<UNTRUSTED[^>]*>>>|<<<END_UNTRUSTED>>>|\[warning:[^\]]*\]")] private static partial Regex Wrapper();

    private static bool Has(ChatRequest r, string tool) => r.Tools?.Any(t => t.Name == tool) == true;
    private static string Clean(string? s) => Wrapper().Replace(s ?? "", "").Trim();
    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    public ChatResponse Respond(ChatRequest request)
    {
        var last = request.Messages.Last(m => m.Role != ChatRole.System);
        var userText = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? "";
        var topic = ResearchIntent().Match(userText) is { Success: true } rm ? rm.Groups[1].Value.Trim().TrimEnd('.', '?', '!') : userText.Trim();

        if (last.Role == ChatRole.Tool)
        {
            var result = Clean(last.Content);
            switch (last.ToolName)
            {
                case "web_search" when Has(request, "web_fetch") && Url().Match(result) is { Success: true } u:
                    return ScriptedProvider.Call(request, "web_fetch", new { url = u.Value.TrimEnd('.', ',') }, "I found sources. Reading the first one.");
                case "web_search":
                    return ScriptedProvider.Text(request, $"Search results for \"{topic}\":\n{Clip(result, 800)}");
                case "web_fetch" when Has(request, "memory_write"):
                    return ScriptedProvider.Call(request, "memory_write",
                        new { content = $"Research on {topic}: {Clip(result.ReplaceLineEndings(" "), 400)}", kind = "semantic", importance = 0.7 },
                        "Saving the key findings to memory.");
                case "web_fetch":
                    return ScriptedProvider.Text(request, $"Summary of {topic}:\n{Clip(result, 800)}");
                case "memory_write":
                {
                    var fetched = Clean(request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolName == "web_fetch")?.Content);
                    return ScriptedProvider.Text(request, $"Summary of {topic}:\n{Clip(fetched.ReplaceLineEndings(" "), 600)}\n\nI saved these findings to memory.");
                }
                case "memory_search":
                    return ScriptedProvider.Text(request, result.Contains("No memories", StringComparison.OrdinalIgnoreCase)
                        ? "I have nothing stored about that yet."
                        : $"From memory:\n{Clip(result, 900)}");
                case "schedule_create":
                    return ScriptedProvider.Text(request, $"Done. {Clip(result, 400)}");
                default:
                    return ScriptedProvider.Text(request, $"{last.ToolName} returned:\n{Clip(result, 900)}");
            }
        }

        if (ScheduleIntent().IsMatch(userText) && Has(request, "schedule_create"))
        {
            var prompt = userText;
            if (InDelay().Match(userText) is { Success: true } d)
                return ScriptedProvider.Call(request, "schedule_create", new { name = Clip(userText, 60), kind = "at", in_seconds = Seconds(d), prompt });
            if (EveryN().Match(userText) is { Success: true } e)
                return ScriptedProvider.Call(request, "schedule_create", new { name = Clip(userText, 60), kind = "every", every_seconds = Seconds(e), prompt });
            if (Daily().Match(userText) is { Success: true } day)
            {
                var hour = day.Groups[1].Success ? int.Parse(day.Groups[1].Value) : 8;
                if (day.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour < 12) hour += 12;
                var minute = day.Groups[2].Success ? int.Parse(day.Groups[2].Value) : 0;
                return ScriptedProvider.Call(request, "schedule_create", new { name = Clip(userText, 60), kind = "cron", cron = $"{minute} {hour} * * *", prompt });
            }
            return ScriptedProvider.Call(request, "schedule_create", new { name = Clip(userText, 60), kind = "at", in_seconds = 3600, prompt });
        }
        if (ResearchIntent().IsMatch(userText) && Has(request, "web_search"))
            return ScriptedProvider.Call(request, "web_search", new { query = topic }, $"Searching the web for \"{topic}\".");
        if (RecallIntent().IsMatch(userText) && Has(request, "memory_search"))
            return ScriptedProvider.Call(request, "memory_search", new { query = userText });
        return ScriptedProvider.Text(request, $"You said: {userText}");
    }

    private static int Seconds(Match m) => int.Parse(m.Groups[1].Value) * (m.Groups[2].Value.ToLowerInvariant() switch { "second" => 1, "minute" => 60, _ => 3600 });
}
