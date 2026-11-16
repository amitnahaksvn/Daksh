using Microsoft.AspNetCore.SignalR;

namespace Daksh.Gateway.Hubs;

/// <summary>
/// Hub side of the device-pairing protocol the reference node (<c>Daksh.Node.Desktop</c>) already speaks
/// (DV-002). Pending MongoDB rebuild: device lookup/approval state was previously checked against
/// PlatformDbContext.Devices in <see cref="OnConnectedAsync"/>; every connection is refused until that's
/// rebuilt.
/// </summary>
public sealed class NodeHub : Hub
{
    public override Task OnConnectedAsync() => throw new HubException("Device pairing is unavailable: pending MongoDB rebuild.");

    public static string GroupName(Guid deviceId) => $"device:{deviceId:N}";
}
