using Daksh.Platform.Domain;

namespace Daksh.Platform.Channels;

/// <summary>
/// Pending MongoDB rebuild: previously turned an inbound channel message into a session and (per the agent's
/// queue mode) a run against PlatformDbContext. Throws until the Mongo-backed implementation is written.
/// </summary>
public sealed class ChannelService
{
    public Task<ChannelMessage> ReceiveAsync(Guid workspaceId, Guid channelAccountId, InboundMessage inbound, CancellationToken ct) =>
        throw new NotImplementedException("ChannelService is unavailable: pending MongoDB rebuild.");
}
