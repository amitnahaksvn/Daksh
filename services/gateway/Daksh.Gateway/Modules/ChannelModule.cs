using Daksh.Gateway.Auth;
using Daksh.Platform.Channels.Core;
using Daksh.Platform.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Daksh.Gateway.Modules;

public sealed record CreateMockChannelAccountRequest(string Name, Guid DefaultAgentId, string DmPolicy = "open");
public sealed record MockChannelAccountDto(Guid ChannelId, Guid AccountId);
public sealed record InjectInboundRequest(string ChatId, string SenderId, string? SenderName, string Text, string ChatType = "dm", bool MentionsBot = true);
public sealed record OutboundDto(string ChatId, string Text);

/// <summary>
/// Real channel adapters (Telegram, Discord, Slack, WhatsApp) expose their own webhook endpoints elsewhere; this
/// module is the mock channel's control surface, used by the automated end-to-end scenario and channel tests (CH-019).
/// Account creation and inbound injection are pending MongoDB rebuild (previously backed by PlatformDbContext);
/// outbound inspection still works since <see cref="MockChannelAdapter"/> keeps sent messages in memory.
/// </summary>
public static class ChannelModule
{
    public static void MapChannels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/testing/mock-channel").RequireAuthorization().WithTags("Testing");

        g.MapPost("/accounts", (CreateMockChannelAccountRequest req) =>
            Results.Problem(statusCode: 501, title: "Mock channel accounts are unavailable: pending MongoDB rebuild."))
            .RequirePermission(Permissions.ChannelsManage);

        g.MapPost("/accounts/{accountId:guid}/inbound", (Guid accountId, InjectInboundRequest req) =>
            Results.Problem(statusCode: 501, title: "Mock channel accounts are unavailable: pending MongoDB rebuild."))
            .RequirePermission(Permissions.ChannelsManage);

        g.MapGet("/accounts/{accountId:guid}/outbound", (Guid accountId, [FromServices] MockChannelAdapter adapter) =>
            Results.Ok(adapter.SentFor(accountId).Select(m => new OutboundDto(m.ChatId, m.Text))))
            .RequirePermission(Permissions.ChannelsRead);
    }

    /// <summary>Public provider webhooks. Message persistence/agent routing is handled by ChannelService once its MongoDB implementation is restored.</summary>
    public static void MapProviderChannelWebhooks(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<IConfiguration>();
        MapProviderWebhook(app, config["Channels:Telegram:WebhookPath"] ?? "/api/v1/webhooks/telegram", "telegram", "Channels:Telegram", "BotToken", "WebhookSecretToken", "telegram");
        MapProviderWebhook(app, config["Channels:Slack:WebhookPath"] ?? "/api/v1/webhooks/slack", "slack", "Channels:Slack", "BotToken", "SigningSecret", "slack");
    }

    private static void MapProviderWebhook(IEndpointRouteBuilder app, string path, string kind, string section,
        string tokenKey, string secretKey, string adapterKind)
    {
        app.MapPost(path, async (HttpContext http, IConfiguration config, IHttpClientFactory clients,
            IEnumerable<Daksh.Platform.Channels.IChannelAdapter> adapters, ILoggerFactory logs) =>
        {
            var settings = config.GetSection(section);
            if (!settings.GetValue<bool>("Enabled")) return Results.NotFound();
            var token = settings[tokenKey];
            var secret = settings[secretKey];
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
                return Results.Problem(statusCode: 503, title: $"{kind} credentials are not configured.");

            await using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            var headers = http.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var request = new Daksh.Platform.Channels.WebhookRequest(http.Request.Method, headers,
                http.Request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase), buffer.ToArray());
            var adapter = adapters.First(a => a.Kind == adapterKind);
            var account = new Daksh.Platform.Channels.ChannelAccountContext
            {
                AccountId = Guid.Empty,
                WorkspaceId = Guid.Empty,
                WebhookUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}",
                Credentials = new Dictionary<string, string> { [tokenKey] = token, [secretKey] = secret },
                Settings = new Dictionary<string, string>(),
                Http = clients.CreateClient($"channel-{adapterKind}")
            };
            var result = await adapter.HandleWebhookAsync(account, request, http.RequestAborted);
            if (result.Messages.Count > 0)
                logs.CreateLogger("Daksh.Channels.Webhooks").LogWarning(
                    "Accepted {Count} inbound {Channel} message(s), but agent delivery is unavailable until ChannelService MongoDB persistence/routing is implemented.",
                    result.Messages.Count, adapterKind);
            http.Response.StatusCode = result.Status;
            http.Response.ContentType = result.ContentType;
            if (!string.IsNullOrEmpty(result.Body)) await http.Response.WriteAsync(result.Body, http.RequestAborted);
            return Results.Empty;
        }).AllowAnonymous().WithTags("Channel webhooks");
    }

}
