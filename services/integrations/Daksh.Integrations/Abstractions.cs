namespace Daksh.Integrations;

/// <summary>Honest operational state of an adapter. Mock adapters never report Verified.</summary>
public enum IntegrationState { Mock, NotConfigured, ConfiguredUnverified, Verified }

public sealed record IntegrationInfo(string Key, string Category, string Provider, IntegrationState State, string Note);

public interface IIntegration
{
    IntegrationInfo Info { get; }
}

public interface ISpeechRecognizer : IIntegration
{
    Task<string> TranscribeAsync(Stream audio, string contentType, CancellationToken ct = default);
}

public interface ISpeechSynthesizer : IIntegration
{
    Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default);
}

public sealed record EmailMessage(string Id, string From, string Subject, string Snippet, DateTimeOffset ReceivedAt);
public interface IEmailProvider : IIntegration
{
    Task<IReadOnlyList<EmailMessage>> ListInboxAsync(string userId, int max, CancellationToken ct = default);
    Task SendAsync(string userId, string to, string subject, string body, CancellationToken ct = default);
}

public sealed record CalendarEvent(string Id, string Title, DateTimeOffset Start, DateTimeOffset End);
public interface ICalendarProvider : IIntegration
{
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<CalendarEvent> CreateEventAsync(string userId, string title, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}

public interface IMessagingProvider : IIntegration
{
    Task SendAsync(string userId, string channel, string recipient, string text, CancellationToken ct = default);
}

public sealed record PushNotification(string UserId, string Title, string Body);
public interface IPushNotificationSender : IIntegration
{
    Task SendAsync(PushNotification notification, CancellationToken ct = default);
}

public interface IWorkflowRunner : IIntegration
{
    Task<string> TriggerAsync(string workflowKey, string jsonPayload, CancellationToken ct = default);
}
