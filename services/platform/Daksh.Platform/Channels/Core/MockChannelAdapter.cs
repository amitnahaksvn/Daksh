namespace Daksh.Platform.Channels.Core;

/// <summary>
/// In-memory channel adapter used for integration and end-to-end tests (CH-019): it has no external dependency and
/// simply records what was sent; inbound messages are injected directly through <see cref="ChannelService"/>.
/// </summary>
public sealed class MockChannelAdapter : IChannelAdapter
{
    public string Kind => "mock";
    public string DisplayName => "Mock channel (testing)";
    public ChannelCapabilities Capabilities => ChannelCapabilities.Send | ChannelCapabilities.AttachmentsIn | ChannelCapabilities.AttachmentsOut
        | ChannelCapabilities.DirectMessages | ChannelCapabilities.Groups | ChannelCapabilities.Mentions;
    public int MaxMessageLength => 4000;
    public IReadOnlyList<CredentialField> CredentialFields => [];
    public string SetupGuide => "No setup required; this adapter is for automated tests.";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, List<OutboundMessage>> _sent = new();
    public IReadOnlyList<OutboundMessage> SentFor(Guid accountId) => _sent.GetValueOrDefault(accountId, []);

    public Task<ChannelProbe> ProbeAsync(ChannelAccountContext account, CancellationToken ct) => Task.FromResult(new ChannelProbe(true, "mock"));

    public Task<SendResult> SendAsync(ChannelAccountContext account, OutboundMessage message, CancellationToken ct)
    {
        _sent.GetOrAdd(account.AccountId, _ => []).Add(message);
        return Task.FromResult(new SendResult(Guid.NewGuid().ToString("N")));
    }
}
