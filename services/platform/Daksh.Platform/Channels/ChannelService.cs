using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Runtime;
using Daksh.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Platform.Channels;

/// <summary>
/// Turns an inbound channel message into a session and (per the agent's queue mode) a run, applying DM/group policy
/// in code before any model is involved (CH-021, CH-022), and wraps the sender's text as untrusted content.
/// </summary>
public sealed class ChannelService(PlatformDbContext db, ExecutionQueueService queue, Infrastructure.IClock clock)
{
    public async Task<ChannelMessage> ReceiveAsync(Guid workspaceId, Guid channelAccountId, InboundMessage inbound, CancellationToken ct)
    {
        var dedupeKey = inbound.ExternalMessageId;
        var existing = await db.ChannelMessages.FirstOrDefaultAsync(
            m => m.ChannelAccountId == channelAccountId && m.Direction == "in" && m.ExternalMessageId == dedupeKey, ct);
        if (existing is not null) return existing;

        var account = await db.ChannelAccounts.Include(a => a.Channel).FirstAsync(a => a.Id == channelAccountId, ct);
        var record = new ChannelMessage
        {
            WorkspaceId = workspaceId, ChannelAccountId = channelAccountId, Direction = "in", ExternalMessageId = inbound.ExternalMessageId,
            ExternalChatId = inbound.ChatId, ExternalSenderId = inbound.SenderId, ExternalThreadId = inbound.ThreadId,
            Status = "received", Text = inbound.Text,
        };
        db.ChannelMessages.Add(record);

        var identity = await db.ChannelIdentities.FirstOrDefaultAsync(i => i.ChannelAccountId == channelAccountId && i.ExternalSenderId == inbound.SenderId, ct);
        var decision = ChannelAccessPolicy.Decide(account.Channel!.Policy, inbound, identity?.Status);
        if (decision.Verdict != InboundVerdict.Accept)
        {
            record.Status = decision.Verdict == InboundVerdict.PairingRequired ? "pending_pairing" : "ignored";
            record.Reason = decision.Reason;
            await db.SaveChangesAsync(ct);
            return record;
        }

        var route = await db.ChannelRoutes
            .Where(r => r.ChannelAccountId == channelAccountId || r.ChannelAccountId == null)
            .OrderByDescending(r => r.ChannelAccountId != null).ThenBy(r => r.Priority)
            .FirstOrDefaultAsync(r => r.ChatType == "any" || r.ChatType == (inbound.ChatType == "dm" ? "dm" : "group"), ct);
        var agentId = route?.AgentId ?? account.DefaultAgentId;
        if (agentId is null)
        {
            record.Status = "ignored"; record.Reason = "no agent is routed to this channel";
            await db.SaveChangesAsync(ct);
            return record;
        }
        var agent = await db.Agents.FindAsync([agentId.Value], ct);
        if (agent is null || !agent.Enabled)
        {
            record.Status = "ignored"; record.Reason = "the routed agent is disabled";
            await db.SaveChangesAsync(ct);
            return record;
        }

        var sessionKey = ChannelText.SessionKey(agent.Id, "per-channel-peer", account.Kind, account.Id, inbound.ChatType, inbound.ChatId, inbound.SenderId);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Key == sessionKey, ct);
        if (session is null)
        {
            session = new Session
            {
                WorkspaceId = workspaceId, AgentId = agent.Id, Key = sessionKey,
                Kind = inbound.ChatType == "dm" ? SessionKind.Direct : SessionKind.Group,
                ChannelAccountId = account.Id, PeerId = inbound.ChatId, Title = $"{account.Channel.Kind}: {inbound.SenderName ?? inbound.SenderId}",
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(ct);
        }

        var message = new Message
        {
            WorkspaceId = workspaceId, SessionId = session.Id, Seq = session.NextSeq++, Role = "user",
            Content = Untrusted.Wrap($"{account.Channel.Kind}:{inbound.SenderName ?? inbound.SenderId}", inbound.Text), Untrusted = true, Epoch = session.Epoch,
        };
        db.Messages.Add(message);
        session.LastMessageAt = clock.UtcNow;
        record.SessionId = session.Id;
        record.Status = "routed";
        await db.SaveChangesAsync(ct);

        var hasActive = await queue.HasActiveRunAsync(session.Id, ct);
        if (!hasActive)
        {
            var execution = await queue.StartAsync(session, agent, message.Seq, "channel", null, null, 0, null, null,
                System.Text.Json.JsonSerializer.Serialize(new { kind = "channel", channel_account_id = account.Id, chat_id = inbound.ChatId }), ct);
            record.ExecutionId = execution.Id;
            await db.SaveChangesAsync(ct);
        }
        return record;
    }
}
