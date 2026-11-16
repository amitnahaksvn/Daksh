using System.Net.Http.Json;
using System.Text.Json;
using Daksh.Platform.Channels;

namespace Daksh.Platform.Channels.Core;

public sealed class TelegramChannelAdapter : IChannelAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Kind => "telegram";
    public string DisplayName => "Telegram";
    public ChannelCapabilities Capabilities => ChannelCapabilities.Send | ChannelCapabilities.Webhook | ChannelCapabilities.DirectMessages | ChannelCapabilities.Groups | ChannelCapabilities.Replies | ChannelCapabilities.Typing;
    public int MaxMessageLength => 4096;
    public IReadOnlyList<CredentialField> CredentialFields =>
    [
        new("BotToken", "Bot token", true, true, "Create a bot with @BotFather."),
        new("WebhookSecretToken", "Webhook secret token", true, true, "A secret value configured when setting the Telegram webhook.")
    ];
    public string SetupGuide => "Create a bot with @BotFather, configure BotToken, then register the webhook URL with Telegram and the same secret token.";

    public async Task<ChannelProbe> ProbeAsync(ChannelAccountContext account, CancellationToken ct)
    {
        try
        {
            using var response = await account.Http.GetAsync(Base(account) + "/getMe", ct);
            var payload = await response.Content.ReadFromJsonAsync<TelegramResponse<TelegramUser>>(JsonOptions, ct);
            return response.IsSuccessStatusCode && payload?.Ok == true
                ? new(true, payload.Result?.Username ?? payload.Result?.FirstName)
                : new(false, Error: payload?.Description ?? response.ReasonPhrase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new(false, Error: ex.Message); }
    }

    public async Task<SendResult> SendAsync(ChannelAccountContext account, OutboundMessage message, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var part in ChannelText.Chunk(Format(message.Text), MaxMessageLength))
        {
            var payload = new Dictionary<string, object?> { ["chat_id"] = message.ChatId, ["text"] = part, ["disable_web_page_preview"] = false };
            if (!string.IsNullOrWhiteSpace(message.ReplyToId) && long.TryParse(message.ReplyToId, out var replyId))
                payload["reply_parameters"] = new { message_id = replyId };
            using var response = await account.Http.PostAsJsonAsync(Base(account) + "/sendMessage", payload, JsonOptions, ct);
            var result = await response.Content.ReadFromJsonAsync<TelegramResponse<TelegramMessage>>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode || result?.Ok != true || result.Result is null)
                throw new ChannelException(result?.Description ?? $"Telegram send failed ({(int)response.StatusCode}).", response.StatusCode >= System.Net.HttpStatusCode.InternalServerError);
            ids.Add(result.Result.MessageId.ToString());
        }
        return new(string.Join(",", ids));
    }

    public async Task TypingAsync(ChannelAccountContext account, string chatId, CancellationToken ct)
    {
        using var response = await account.Http.PostAsJsonAsync(Base(account) + "/sendChatAction", new { chat_id = chatId, action = "typing" }, ct);
        response.EnsureSuccessStatusCode();
    }

    public Task<WebhookResult> HandleWebhookAsync(ChannelAccountContext account, WebhookRequest request, CancellationToken ct)
    {
        if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var supplied) ||
            !ConstantEquals(supplied, account.Credential("WebhookSecretToken")))
            return Task.FromResult(WebhookResult.Reject(401, "Invalid webhook secret."));
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            if (!doc.RootElement.TryGetProperty("message", out var msg) &&
                !doc.RootElement.TryGetProperty("edited_message", out msg))
                return Task.FromResult(WebhookResult.Ok());
            var chat = msg.GetProperty("chat");
            var from = msg.GetProperty("from");
            var text = msg.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(WebhookResult.Ok());
            var chatTypeRaw = chat.GetProperty("type").GetString() ?? "private";
            var chatType = chatTypeRaw == "private" ? "dm" : "group";
            var name = string.Join(" ", new[] { from.TryGetProperty("first_name", out var fn) ? fn.GetString() : null, from.TryGetProperty("last_name", out var ln) ? ln.GetString() : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var inbound = new InboundMessage(
                msg.GetProperty("message_id").GetInt64().ToString(), chat.GetProperty("id").GetInt64().ToString(),
                chatType, from.GetProperty("id").GetInt64().ToString(), name,
                from.TryGetProperty("is_bot", out var bot) && bot.GetBoolean(), text,
                Timestamp: msg.TryGetProperty("date", out var date) ? DateTimeOffset.FromUnixTimeSeconds(date.GetInt64()) : null);
            return Task.FromResult(WebhookResult.Ok([inbound]));
        }
        catch (JsonException) { return Task.FromResult(WebhookResult.Reject(400, "Invalid Telegram update.")); }
    }

    public string Format(string markdown) => ChannelText.StripMarkdown(markdown);
    private static string Base(ChannelAccountContext account) => "https://api.telegram.org/bot" + account.Credential("BotToken");
    private static bool ConstantEquals(string a, string b) => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    private sealed record TelegramResponse<T>(bool Ok, T? Result, string? Description);
    private sealed record TelegramUser(long Id, string? FirstName, string? Username);
    private sealed record TelegramMessage(long MessageId);
}
