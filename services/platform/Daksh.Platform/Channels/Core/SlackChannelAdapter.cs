using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Platform.Channels;

namespace Daksh.Platform.Channels.Core;

public sealed class SlackChannelAdapter : IChannelAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Kind => "slack";
    public string DisplayName => "Slack";
    public ChannelCapabilities Capabilities => ChannelCapabilities.Send | ChannelCapabilities.Webhook | ChannelCapabilities.DirectMessages | ChannelCapabilities.Groups | ChannelCapabilities.Threads | ChannelCapabilities.Replies;
    public int MaxMessageLength => 40000;
    public IReadOnlyList<CredentialField> CredentialFields =>
    [
        new("BotToken", "Bot token", true, true, "Create a Slack app and install it to your workspace."),
        new("SigningSecret", "Signing secret", true, true, "Copy this from your Slack app's Basic Information page.")
    ];
    public string SetupGuide => "Create a Slack app, add chat:write and event subscription scopes, install it, and configure the Events Request URL with SigningSecret verification.";

    public async Task<ChannelProbe> ProbeAsync(ChannelAccountContext account, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Credential("BotToken"));
            using var response = await account.Http.SendAsync(request, ct);
            var result = await response.Content.ReadFromJsonAsync<SlackResponse>(JsonOptions, ct);
            return response.IsSuccessStatusCode && result?.Ok == true
                ? new(true, result.User ?? result.Team)
                : new(false, Error: result?.Error ?? response.ReasonPhrase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, Error: ex.Message); }
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundMessage message, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var part in ChannelText.Chunk(Format(message.Text), MaxMessageLength))
        {
            var payload = new Dictionary<string, object?> { ["channel"] = message.ChatId, ["text"] = part };
            if (!string.IsNullOrWhiteSpace(message.ThreadId)) payload["thread_ts"] = message.ThreadId;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Credential("BotToken"));
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            using var response = await account.Http.SendAsync(request, ct);
            var result = await response.Content.ReadFromJsonAsync<SlackResponse>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode || result?.Ok != true)
                throw new ChannelException(result?.Error ?? $"Slack send failed ({(int)response.StatusCode}).", response.StatusCode >= System.Net.HttpStatusCode.InternalServerError);
            if (!string.IsNullOrEmpty(result.Ts)) ids.Add(result.Ts);
        }
        return new(string.Join(",", ids));
    }

    public Task<WebhookResult> HandleWebhookAsync(ChannelAccountContext account, WebhookRequest request, CancellationToken ct)
    {
        if (!VerifySignature(account.Credential("SigningSecret"), request.Headers, request.Body))
            return Task.FromResult(WebhookResult.Reject(401, "Invalid Slack signature."));
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "url_verification" &&
                root.TryGetProperty("challenge", out var challenge))
                return Task.FromResult(WebhookResult.Ok(body: JsonSerializer.Serialize(new { challenge = challenge.GetString() }), contentType: "application/json"));
            if (!root.TryGetProperty("event", out var ev) || !ev.TryGetProperty("type", out var eventType) || eventType.GetString() != "message")
                return Task.FromResult(WebhookResult.Ok());
            if (ev.TryGetProperty("subtype", out _) || ev.TryGetProperty("bot_id", out _))
                return Task.FromResult(WebhookResult.Ok());
            var text = ev.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(WebhookResult.Ok());
            var channel = ev.GetProperty("channel").GetString() ?? "";
            var channelType = ev.TryGetProperty("channel_type", out var ctEl) ? ctEl.GetString() : null;
            var thread = ev.TryGetProperty("thread_ts", out var threadEl) ? threadEl.GetString() : null;
            var sender = ev.TryGetProperty("user", out var userEl) ? userEl.GetString() ?? "" : "";
            var inbound = new InboundMessage(
                ev.TryGetProperty("ts", out var ts) ? ts.GetString() ?? "" : "",
                channel, channelType == "im" ? "dm" : "group", sender, null, false, text,
                ThreadId: thread,
                Timestamp: ev.TryGetProperty("ts", out var stamp) && double.TryParse(stamp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null);
            return Task.FromResult(WebhookResult.Ok([inbound]));
        }
        catch (JsonException) { return Task.FromResult(WebhookResult.Reject(400, "Invalid Slack event.")); }
    }

    public string Format(string markdown) => ChannelText.StripMarkdown(markdown);

    private static bool VerifySignature(string secret, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (!headers.TryGetValue("X-Slack-Request-Timestamp", out var timestamp) ||
            !headers.TryGetValue("X-Slack-Signature", out var signature) ||
            !long.TryParse(timestamp, out var unix)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300) return false;
        var baseString = "v0:" + timestamp + ":" + Encoding.UTF8.GetString(body);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(baseString));
        var expected = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    private sealed record SlackResponse(bool Ok, string? Error, string? User, string? Team, string? Ts);
}
