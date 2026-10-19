using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daksh.Integrations;

/// <summary>
/// Deterministic in-process adapters used when credentials or external services are unavailable.
/// They never contact the network and always report <see cref="IntegrationState.Mock"/>.
/// </summary>
public abstract class MockBase(string key, string category)
{
    public IntegrationInfo Info { get; } = new(key, category, "mock", IntegrationState.Mock,
        "Simulated adapter. No external service is contacted; not verified against a real provider.");
}

/// <summary>Simulated chat model implementing the standard <see cref="IChatClient"/>; echoes the last user message.</summary>
public sealed class MockChatClient : IChatClient, IIntegration
{
    public IntegrationInfo Info { get; } = new("ai-chat", "ai", "mock", IntegrationState.Mock,
        "Simulated model. No LLM is contacted; replies are canned echoes.");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var last = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"[mock] I received: {last}"))
        {
            Usage = new UsageDetails { InputTokenCount = last.Length / 4 + 1, OutputTokenCount = last.Length / 4 + 5 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, r.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

/// <summary>Deterministic hashed bag-of-words embedder (64 dims) for tests and offline development. Not semantically meaningful.</summary>
public sealed class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>, IIntegration
{
    public IntegrationInfo Info { get; } = new("ai-embeddings", "ai", "mock", IntegrationState.Mock,
        "Simulated embeddings (hashed words). Not semantic.");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var v in values)
        {
            var vec = new float[64];
            foreach (var w in v.ToLowerInvariant().Split([' ', '.', ',', '!', '?'], StringSplitOptions.RemoveEmptyEntries))
                vec[(int)((uint)w.Aggregate(17, (h, c) => h * 31 + c) % 64)] += 1;
            result.Add(new Embedding<float>(vec));
        }
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

public sealed class MockSpeechRecognizer : MockBase, ISpeechRecognizer
{
    public MockSpeechRecognizer() : base("speech-recognition", "speech") { }
    public Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken ct = default) =>
        Task.FromResult("[mock transcript]");
}

public sealed class MockSpeechSynthesizer : MockBase, ISpeechSynthesizer
{
    public MockSpeechSynthesizer() : base("speech-synthesis", "speech") { }
    public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}

public sealed class MockEmailProvider : MockBase, IEmailProvider
{
    public MockEmailProvider() : base("email", "email") { }
    public Task<IReadOnlyList<EmailMessage>> ListInboxAsync(string userId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EmailMessage>>(new[]
        {
            new EmailMessage("mock-1", "sample@example.invalid", "Sample message (mock)", "This is simulated data.", DateTimeOffset.UtcNow)
        }.Take(max).ToList());
    public Task SendAsync(string userId, string to, string subject, string body, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class MockCalendarProvider : MockBase, ICalendarProvider
{
    public MockCalendarProvider() : base("calendar", "calendar") { }
    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CalendarEvent>>(Array.Empty<CalendarEvent>());
    public Task<CalendarEvent> CreateEventAsync(string userId, string title, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        Task.FromResult(new CalendarEvent($"mock-{Guid.NewGuid():N}", title, start, end));
}

public sealed class MockMessagingProvider : MockBase, IMessagingProvider
{
    public MockMessagingProvider() : base("messaging", "messaging") { }
    public Task SendAsync(string userId, string channel, string recipient, string text, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Records notifications in memory so tests can assert delivery.</summary>
public sealed class MockPushNotificationSender(ILogger<MockPushNotificationSender>? logger = null) : IPushNotificationSender
{
    private readonly List<PushNotification> _sent = new();
    public IReadOnlyList<PushNotification> Sent { get { lock (_sent) return _sent.ToList(); } }
    public IntegrationInfo Info { get; } = new("push", "push", "mock", IntegrationState.Mock,
        "Simulated adapter. No device receives these notifications.");

    public Task SendAsync(PushNotification notification, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(notification);
        logger?.LogInformation("Mock push for user {UserId}: {Title}", notification.UserId, notification.Title);
        return Task.CompletedTask;
    }
}

public sealed class MockWorkflowRunner : MockBase, IWorkflowRunner
{
    public MockWorkflowRunner() : base("n8n", "workflow") { }
    public Task<string> TriggerAsync(string workflowKey, string jsonPayload, CancellationToken ct = default) =>
        Task.FromResult($"mock-run-{Guid.NewGuid():N}");
}
