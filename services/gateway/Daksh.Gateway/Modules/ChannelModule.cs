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
}
