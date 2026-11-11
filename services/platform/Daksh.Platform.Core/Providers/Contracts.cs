using System.Text.Json;
using Daksh.Platform.Domain;

namespace Daksh.Platform.Providers;

public enum ChatRole { System, User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>Non-text content. <see cref="Data"/> is base64.</summary>
public sealed record ContentPart(string Type, string MediaType, string Data);

public sealed record ChatMessage(ChatRole Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null, string? ToolName = null, IReadOnlyList<ContentPart>? Parts = null);

public sealed record ToolSpec(string Name, string Description, JsonElement Schema);

public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSpec>? Tools = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    JsonElement? ResponseSchema = null,
    IReadOnlyDictionary<string, string>? Options = null)
{
    public bool HasImages => Messages.Any(m => m.Parts?.Any(p => p.Type == "image") == true);
}

public sealed record Usage(long InputTokens, long OutputTokens, bool Estimated = false);

public sealed record ChatResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, Usage Usage, string FinishReason, string Model);

public abstract record ChatStreamEvent;
public sealed record TextDelta(string Text) : ChatStreamEvent;
public sealed record ToolCallStarted(int Index, string Id, string Name) : ChatStreamEvent;
public sealed record ChatCompleted(ChatResponse Response) : ChatStreamEvent;

public sealed record ProviderEndpoint(string Kind, string? BaseUrl, string? ApiKey,
    IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> Options);

public sealed record DiscoveredModel(string Id, string? DisplayName = null, int? ContextWindow = null, ModelCapabilities? Capabilities = null);

public sealed record ProviderHealth(bool Healthy, string? Error = null, long LatencyMs = 0);

public enum ProviderErrorKind { Auth, RateLimit, Timeout, Unavailable, BadRequest, ContextLength, Billing, Unknown }

public sealed class ProviderException(ProviderErrorKind kind, string message, int? status = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ProviderErrorKind Kind { get; } = kind;
    public int? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Worth trying the same model again.</summary>
    public bool IsTransient => Kind is ProviderErrorKind.RateLimit or ProviderErrorKind.Timeout or ProviderErrorKind.Unavailable;

    /// <summary>Worth trying another credential or a fallback model.</summary>
    public bool ShouldFailover => Kind is not (ProviderErrorKind.BadRequest or ProviderErrorKind.ContextLength);
}

/// <summary>The request asks for something the chosen model cannot do. Raised before any network call.</summary>
public sealed class CapabilityException(string message) : Exception(message);

public interface IModelProvider
{
    /// <summary>Adapter kinds this implementation serves (for example "openai" and "openai-compatible").</summary>
    IReadOnlyList<string> Kinds { get; }

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderEndpoint endpoint, ChatRequest request, CancellationToken ct);

    /// <summary>Models the endpoint reports. Empty when the API has no listing.</summary>
    Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(ProviderEndpoint endpoint, CancellationToken ct);

    Task<ProviderHealth> CheckAsync(ProviderEndpoint endpoint, CancellationToken ct);

    /// <summary>Embeddings for the inputs, or throws <see cref="CapabilityException"/> when the provider has none.</summary>
    Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(ProviderEndpoint endpoint, string model, IReadOnlyList<string> inputs, CancellationToken ct);
}

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _byKind = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IEnumerable<IModelProvider> providers)
    {
        foreach (var p in providers)
            foreach (var k in p.Kinds) _byKind[k] = p;
    }

    public IReadOnlyCollection<string> Kinds => _byKind.Keys;

    public IModelProvider Get(string kind) =>
        _byKind.TryGetValue(kind, out var p) ? p : throw new InvalidOperationException($"No provider adapter is registered for kind '{kind}'.");

    public bool Supports(string kind) => _byKind.ContainsKey(kind);
}

public static class CapabilityCheck
{
    /// <summary>Rejects a request the model cannot serve instead of letting the provider fail (or silently ignore it).</summary>
    public static void Ensure(ModelCapabilities caps, ChatRequest request, string modelRef)
    {
        if (!caps.Chat) throw new CapabilityException($"Model '{modelRef}' is not a chat model.");
        if (request.Tools is { Count: > 0 } && !caps.Tools)
            throw new CapabilityException($"Model '{modelRef}' does not support tool calling.");
        if (request.HasImages && !caps.Vision)
            throw new CapabilityException($"Model '{modelRef}' does not accept images.");
        if (request.ResponseSchema is not null && !caps.StructuredOutput)
            throw new CapabilityException($"Model '{modelRef}' does not support structured output.");
    }
}

public static class TokenEstimator
{
    /// <summary>Rough count used for budgeting and when a provider reports no usage (about four characters per token).</summary>
    public static int Estimate(string? text) => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (text.Length + 3) / 4);

    public static int Estimate(ChatMessage m) =>
        4 + Estimate(m.Content) + (m.ToolCalls?.Sum(t => Estimate(t.Name) + Estimate(t.Arguments) + 8) ?? 0) + (m.Parts?.Count ?? 0) * 800;

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    public static int Estimate(IEnumerable<ToolSpec>? tools) =>
        tools?.Sum(t => Estimate(t.Name) + Estimate(t.Description) + Estimate(t.Schema.GetRawText())) ?? 0;
}

public static class Pricing
{
    public static decimal Cost(long inputTokens, long outputTokens, decimal inputPerMTok, decimal outputPerMTok) =>
        Math.Round(inputTokens * inputPerMTok / 1_000_000m + outputTokens * outputPerMTok / 1_000_000m, 8);
}
