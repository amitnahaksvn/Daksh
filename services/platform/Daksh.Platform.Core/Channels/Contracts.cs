namespace Daksh.Platform.Channels;

[Flags]
public enum ChannelCapabilities
{
    None = 0, Send = 1, Edit = 2, Delete = 4, Reactions = 8, Threads = 16, Typing = 32, AttachmentsIn = 64, AttachmentsOut = 128,
    Webhook = 256, Polling = 512, Socket = 1024, Groups = 2048, DirectMessages = 4096, Mentions = 8192, Replies = 16384
}

public sealed record InboundAttachment(string Name, string ContentType, long? Size, string? Url, string? ExternalFileId);

public sealed record InboundMessage(
    string ExternalMessageId, string ChatId, string ChatType, string SenderId, string? SenderName, bool SenderIsBot, string Text,
    string? ThreadId = null, string? ReplyToId = null, bool MentionsBot = false,
    IReadOnlyList<InboundAttachment>? Attachments = null, DateTimeOffset? Timestamp = null);

public sealed record OutboundAttachment(string Name, string ContentType, byte[] Content);

public sealed record OutboundMessage(string ChatId, string Text, string? ThreadId = null, string? ReplyToId = null,
    IReadOnlyList<OutboundAttachment>? Attachments = null);

public sealed record SendResult(string ExternalMessageId);

public sealed record ChannelProbe(bool Ok, string? Identity = null, string? Error = null);

public sealed record CredentialField(string Name, string Label, bool Secret, bool Required, string Help);

public sealed record WebhookRequest(string Method, IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> Query, byte[] Body);

public sealed record WebhookResult(int Status, string? Body, string ContentType, IReadOnlyList<InboundMessage> Messages)
{
    public static WebhookResult Ok(IReadOnlyList<InboundMessage>? messages = null, string? body = null, string contentType = "text/plain") =>
        new(200, body, contentType, messages ?? []);
    public static WebhookResult Reject(int status, string reason) => new(status, reason, "text/plain", []);
}

/// <summary>Everything an adapter may use for one configured account.</summary>
public sealed class ChannelAccountContext
{
    public required Guid AccountId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string WebhookUrl { get; init; }
    public required IReadOnlyDictionary<string, string> Credentials { get; init; }
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
    public required HttpClient Http { get; init; }
    /// <summary>Reads and stores the adapter's receive cursor so receiving resumes after a restart.</summary>
    public Func<Task<string?>> LoadCursor { get; init; } = () => Task.FromResult<string?>(null);
    public Func<string, Task> SaveCursor { get; init; } = _ => Task.CompletedTask;

    public string Credential(string name) => Credentials.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v)
        ? v : throw new ChannelException($"Credential '{name}' is not configured.", retryable: false);
}

public sealed class ChannelException(string message, bool retryable = true, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// One messaging platform. The agent runtime never sees adapters: it only produces text, and the channel service
/// turns that into <see cref="OutboundMessage"/>s. Optional operations throw <see cref="NotSupportedException"/>
/// unless the matching capability flag is set.
/// </summary>
public interface IChannelAdapter
{
    string Kind { get; }
    string DisplayName { get; }
    ChannelCapabilities Capabilities { get; }
    int MaxMessageLength { get; }
    IReadOnlyList<CredentialField> CredentialFields { get; }
    string SetupGuide { get; }

    Task<ChannelProbe> ProbeAsync(ChannelAccountContext account, CancellationToken ct);
    Task<SendResult> SendAsync(ChannelAccountContext account, OutboundMessage message, CancellationToken ct);
    Task EditAsync(ChannelAccountContext account, string chatId, string messageId, string text, CancellationToken ct) => throw new NotSupportedException();
    Task DeleteAsync(ChannelAccountContext account, string chatId, string messageId, CancellationToken ct) => throw new NotSupportedException();
    Task ReactAsync(ChannelAccountContext account, string chatId, string messageId, string emoji, CancellationToken ct) => throw new NotSupportedException();
    Task TypingAsync(ChannelAccountContext account, string chatId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Verifies and parses an inbound webhook call. Must reject anything whose signature does not verify.</summary>
    Task<WebhookResult> HandleWebhookAsync(ChannelAccountContext account, WebhookRequest request, CancellationToken ct) =>
        Task.FromResult(WebhookResult.Reject(404, "This channel does not receive webhooks."));

    /// <summary>Long-running receive loop for polling or socket channels. Returns immediately when not applicable.</summary>
    Task ReceiveAsync(ChannelAccountContext account, Func<InboundMessage, Task> onMessage, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Converts the agent's Markdown into what the platform renders.</summary>
    string Format(string markdown) => markdown;
}

public static class ChannelText
{
    /// <summary>Splits text into parts no longer than <paramref name="max"/>, preferring paragraph, line and word boundaries.</summary>
    public static IReadOnlyList<string> Chunk(string text, int max)
    {
        var parts = new List<string>();
        text = text.Trim();
        while (text.Length > max)
        {
            var window = text[..max];
            var cut = window.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (cut < max / 2) cut = window.LastIndexOf('\n');
            if (cut < max / 2) cut = window.LastIndexOf(' ');
            if (cut < max / 4) cut = max;
            parts.Add(text[..cut].TrimEnd());
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) parts.Add(text);
        return parts;
    }

    /// <summary>Removes Markdown markup for platforms that show plain text.</summary>
    public static string StripMarkdown(string md)
    {
        md = System.Text.RegularExpressions.Regex.Replace(md, @"```[a-zA-Z0-9]*\n([\s\S]*?)```", "$1");
        md = System.Text.RegularExpressions.Regex.Replace(md, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
        md = System.Text.RegularExpressions.Regex.Replace(md, @"(\*\*|__)(.+?)\1", "$2");
        md = System.Text.RegularExpressions.Regex.Replace(md, @"(?m)^#{1,6}\s+", "");
        return md.Replace("`", "");
    }

    /// <summary>Session routing key for a channel conversation, per the agent's direct-message scope.</summary>
    public static string SessionKey(Guid agentId, string dmScope, string channelKind, Guid accountId, string chatType, string chatId, string senderId)
    {
        if (chatType != "dm") return $"agent:{agentId:N}:{channelKind}:{accountId:N}:group:{chatId}";
        return dmScope switch
        {
            "main" => $"agent:{agentId:N}:main",
            "per-peer" => $"agent:{agentId:N}:dm:{senderId}",
            "per-account-channel-peer" => $"agent:{agentId:N}:{channelKind}:{accountId:N}:dm:{senderId}",
            _ => $"agent:{agentId:N}:{channelKind}:dm:{senderId}",
        };
    }
}

public enum InboundVerdict { Accept, Ignore, PairingRequired, Blocked }

public sealed record InboundDecision(InboundVerdict Verdict, string Reason);

public static class ChannelAccessPolicy
{
    /// <summary>Decides in code whether an inbound message may reach an agent. Runs before any model is involved.</summary>
    public static InboundDecision Decide(Daksh.Platform.Domain.ChannelPolicy policy, InboundMessage m, string? identityStatus)
    {
        if (m.SenderIsBot && policy.IgnoreBots) return new(InboundVerdict.Ignore, "sender is a bot");
        if (identityStatus == "blocked") return new(InboundVerdict.Blocked, "sender is blocked");
        if (m.ChatType == "dm")
        {
            switch (policy.DmPolicy)
            {
                case "disabled": return new(InboundVerdict.Ignore, "direct messages are disabled");
                case "open": return new(InboundVerdict.Accept, "direct messages are open");
                case "allowlist":
                    return policy.AllowFrom.Contains(m.SenderId) || policy.AllowFrom.Contains("*")
                        ? new(InboundVerdict.Accept, "sender is on the allowlist")
                        : new(InboundVerdict.Ignore, "sender is not on the allowlist");
                default:
                    if (policy.AllowFrom.Contains(m.SenderId) || identityStatus == "approved") return new(InboundVerdict.Accept, "sender is paired");
                    return new(InboundVerdict.PairingRequired, "sender is not paired");
            }
        }
        switch (policy.GroupPolicy)
        {
            case "disabled": return new(InboundVerdict.Ignore, "groups are disabled");
            case "allowlist" when !policy.AllowGroups.Contains(m.ChatId) && !policy.AllowGroups.Contains("*"):
                return new(InboundVerdict.Ignore, "group is not on the allowlist");
        }
        if (policy.RequireMention && !m.MentionsBot &&
            !policy.MentionPatterns.Any(p => m.Text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return new(InboundVerdict.Ignore, "the bot was not mentioned");
        return new(InboundVerdict.Accept, "group message accepted");
    }
}
