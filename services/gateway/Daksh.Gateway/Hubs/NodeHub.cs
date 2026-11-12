using Daksh.Platform.Data;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Hubs;

/// <summary>
/// Hub side of the device-pairing protocol the reference node (<c>Daksh.Node.Desktop</c>) already speaks
/// (DV-002): the node connects here with the opaque device token it got back from
/// <c>POST /api/v1/devices/pairing/claim</c>, supplied as the SignalR client's <c>AccessTokenProvider</c> (the
/// node's code: <c>new HubConnectionBuilder().WithUrl($"{gatewayUrl}/hubs/node", o =&gt; o.AccessTokenProvider =
/// () =&gt; Task.FromResult(token))</c>). The Gateway later calls the client's "invoke" method
/// (<c>connection.On&lt;string, string&gt;("invoke", ...)</c> on the node side) to run a capability (DV-004),
/// via <see cref="Modules.DeviceModule"/>'s invoke endpoint.
///
/// Authentication here is device-token based, not JWT, so this hub is intentionally not [Authorize]'d against
/// the JwtBearer scheme that protects <see cref="GatewayHub"/>: <see cref="OnConnectedAsync"/> does its own
/// lookup by hashing the presented token and rejects anything that isn't a known, approved, non-revoked
/// device - which is also how approval and revocation take effect for the hub side of pairing (DV-001):
/// approving a device flips its Status to "active" so a connection can succeed; revoking flips it away from
/// "active" so both a fresh connection and any further invoke are rejected, even though a prior connection may
/// briefly remain technically open.
/// </summary>
public sealed class NodeHub(PlatformDbContext db) : Hub
{
    private const string DeviceIdKey = "deviceId";

    public override async Task OnConnectedAsync()
    {
        var token = ExtractToken(Context.GetHttpContext());
        if (string.IsNullOrEmpty(token))
            throw new HubException("No device token was presented.");

        // The token alone doesn't name a workspace - this is the same system-scoped lookup pattern
        // RuntimeWorker/JobRunnerWorker/ChannelOutboxWorker use to touch workspace-scoped rows outside any
        // one resolved tenant.
        db.Tenant.SetSystem();
        var hash = Tokens.Hash(token);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash);
        if (device is null || device.Status != "active")
            throw new HubException("Device is unknown, not yet approved, or has been revoked.");

        device.Online = true;
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Context.Items[DeviceIdKey] = device.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(device.Id));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(DeviceIdKey, out var raw) && raw is Guid deviceId)
        {
            db.Tenant.SetSystem();
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId);
            if (device is not null)
            {
                device.Online = false;
                await db.SaveChangesAsync();
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Every connected device joins exactly this group, so the invoke endpoint can target it without
    /// tracking connection ids itself.</summary>
    public static string GroupName(Guid deviceId) => $"device:{deviceId:N}";

    private static string? ExtractToken(HttpContext? http)
    {
        if (http is null) return null;
        var fromQuery = http.Request.Query["access_token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : null;
    }
}
