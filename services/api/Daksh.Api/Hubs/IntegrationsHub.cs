using Daksh.Api.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Daksh.Api.Hubs;

/// <summary>
/// Pushes WhatsApp (and, later, other integration) connection-lifecycle events to the owning user only.
/// Every connected client is placed in a group named after its own user id (from the validated JWT),
/// never a group it asks for — so a client can only ever receive its own events.
/// </summary>
public sealed class IntegrationsHub : Hub
{
    public static string UserGroup(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.User!.UserId()));
        await base.OnConnectedAsync();
    }
}

public static class IntegrationEvents
{
    public const string ConnectionStarted = "WhatsAppConnectionStarted";
    public const string QrGenerated = "WhatsAppQrGenerated";
    public const string QrExpired = "WhatsAppQrExpired";
    public const string Connected = "WhatsAppConnected";
    public const string Disconnected = "WhatsAppDisconnected";
    public const string ConnectionFailed = "WhatsAppConnectionFailed";

    public static Task PublishAsync(IHubContext<IntegrationsHub> hub, string userId, string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(IntegrationsHub.UserGroup(userId)).SendAsync(eventName, payload, ct);
}
