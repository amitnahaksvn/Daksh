using Daksh.Gateway.Auth;
using Daksh.Platform.Channels;
using Daksh.Platform.Channels.Core;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record CreateMockChannelAccountRequest(string Name, Guid DefaultAgentId, string DmPolicy = "open");
public sealed record MockChannelAccountDto(Guid ChannelId, Guid AccountId);
public sealed record InjectInboundRequest(string ChatId, string SenderId, string? SenderName, string Text, string ChatType = "dm", bool MentionsBot = true);
public sealed record OutboundDto(string ChatId, string Text);

/// <summary>
/// Real channel adapters (Telegram, Discord, Slack, WhatsApp) expose their own webhook endpoints elsewhere; this
/// module is the mock channel's control surface, used by the automated end-to-end scenario and channel tests (CH-019).
/// </summary>
public static class ChannelModule
{
    public static void MapChannels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/testing/mock-channel").RequireAuthorization().WithTags("Testing");

        g.MapPost("/accounts", async (CreateMockChannelAccountRequest req, PlatformDbContext db, CancellationToken ct) =>
        {
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Kind == "mock" && c.Name == req.Name, ct);
            if (channel is null)
            {
                channel = new Channel { Kind = "mock", Name = req.Name, Policy = new ChannelPolicy { DmPolicy = req.DmPolicy, GroupPolicy = "open", RequireMention = false } };
                db.Channels.Add(channel);
            }
            var account = new ChannelAccount { ChannelId = channel.Id, Kind = "mock", Name = req.Name, DefaultAgentId = req.DefaultAgentId, WebhookKey = Guid.NewGuid().ToString("N") };
            db.ChannelAccounts.Add(account);
            db.ChannelRoutes.Add(new ChannelRoute { ChannelAccountId = account.Id, ChatType = "any", AgentId = req.DefaultAgentId });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MockChannelAccountDto(channel.Id, account.Id));
        }).RequirePermission(Permissions.ChannelsManage);

        g.MapPost("/accounts/{accountId:guid}/inbound", async (Guid accountId, InjectInboundRequest req, ChannelService service, PlatformDbContext db, CancellationToken ct) =>
        {
            var account = await db.ChannelAccounts.FindAsync([accountId], ct);
            if (account is null) return Results.NotFound();
            var inbound = new InboundMessage(Guid.NewGuid().ToString("N"), req.ChatId, req.ChatType, req.SenderId, req.SenderName, false, req.Text, MentionsBot: req.MentionsBot);
            var record = await service.ReceiveAsync(account.WorkspaceId, accountId, inbound, ct);
            return Results.Ok(new { record.Id, record.Status, record.SessionId, record.ExecutionId });
        }).RequirePermission(Permissions.ChannelsManage);

        g.MapGet("/accounts/{accountId:guid}/outbound", (Guid accountId, [FromServices] MockChannelAdapter adapter) =>
            Results.Ok(adapter.SentFor(accountId).Select(m => new OutboundDto(m.ChatId, m.Text))))
            .RequirePermission(Permissions.ChannelsRead);
    }
}
