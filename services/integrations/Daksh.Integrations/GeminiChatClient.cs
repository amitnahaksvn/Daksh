using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Daksh.Integrations;

/// <summary>
/// Real <see cref="IChatClient"/> backed by Google's Gemini `generateContent` REST API. Registered as a
/// Cloud-locality <c>ChatModelRegistration</c> (see ServiceCollectionExtensions), so Daksh.Ai's
/// PolicyModelRouter only ever selects it for a user who has allowed provider data sharing — the same
/// consent gate every other cloud model goes through.
/// </summary>
public sealed class GeminiChatClient(HttpClient http, string apiKey, string model) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var all = messages.ToList();
        var systemText = string.Join("\n\n", all.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var contents = all
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new GeminiContent(m.Role == ChatRole.Assistant ? "model" : "user", [new GeminiPart(m.Text ?? "")]))
            .ToList();

        var request = new GeminiRequest(
            string.IsNullOrWhiteSpace(systemText) ? null : new GeminiSystemInstruction([new GeminiPart(systemText)]),
            contents);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        // 429 (rate limited) and 503 (momentarily overloaded) are the two errors Gemini's free tier returns
        // under normal, expected load — both are worth one short retry instead of failing the whole turn.
        HttpResponseMessage res;
        var attempt = 0;
        while (true)
        {
            res = await http.PostAsJsonAsync(url, request, cancellationToken);
            if (res.IsSuccessStatusCode) break;
            var transient = res.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable;
            if (!transient || attempt >= 2)
            {
                res.EnsureSuccessStatusCode();
                break; // unreachable — EnsureSuccessStatusCode always throws here
            }
            res.Dispose();
            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
        }
        using var _ = res;

        var body = await res.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Gemini returned an empty response.");
        // A "thinking" model (e.g. gemini-3.x) can split its answer across multiple parts and mark some as
        // internal reasoning — join every non-thought part rather than assume the first part is the whole answer.
        var text = string.Concat(
            (body.Candidates?.FirstOrDefault()?.Content?.Parts ?? [])
                .Where(p => !p.Thought)
                .Select(p => p.Text));

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = body.UsageMetadata?.PromptTokenCount ?? 0,
                OutputTokenCount = body.UsageMetadata?.CandidatesTokenCount ?? 0,
            },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, r.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() => http.Dispose();
}

file sealed record GeminiPart([property: JsonPropertyName("text")] string Text);
file sealed record GeminiContent([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("parts")] List<GeminiPart> Parts);
file sealed record GeminiSystemInstruction([property: JsonPropertyName("parts")] List<GeminiPart> Parts);
file sealed record GeminiRequest(
    [property: JsonPropertyName("systemInstruction")] GeminiSystemInstruction? SystemInstruction,
    [property: JsonPropertyName("contents")] List<GeminiContent> Contents);
file sealed record GeminiUsage(
    [property: JsonPropertyName("promptTokenCount")] int? PromptTokenCount,
    [property: JsonPropertyName("candidatesTokenCount")] int? CandidatesTokenCount);
file sealed record GeminiResponsePart([property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("thought")] bool Thought = false);
file sealed record GeminiCandidateContent([property: JsonPropertyName("parts")] List<GeminiResponsePart>? Parts);
file sealed record GeminiCandidate([property: JsonPropertyName("content")] GeminiCandidateContent? Content);
file sealed record GeminiResponse(
    [property: JsonPropertyName("candidates")] List<GeminiCandidate>? Candidates,
    [property: JsonPropertyName("usageMetadata")] GeminiUsage? UsageMetadata);
