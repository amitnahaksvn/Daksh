using Daksh.Platform.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Daksh.Gateway.Hubs;

/// <summary>Real-time surface: clients join a session group to receive its run events (text, tool activity, approvals).</summary>
[Authorize]
public sealed class GatewayHub : Hub
{
    // Pending MongoDB rebuild: previously looked up the session via PlatformDbContext.Sessions.
    public Task JoinSession(Guid sessionId) => throw new HubException("Sessions are unavailable: pending MongoDB rebuild.");

    public Task LeaveSession(Guid workspaceId, Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(workspaceId, sessionId));

    public static string GroupName(Guid workspaceId, Guid sessionId) => $"session:{workspaceId:N}:{sessionId:N}";
    public static string WorkspaceGroup(Guid workspaceId) => $"workspace:{workspaceId:N}";
}

/// <summary>Publishes run events to the session's SignalR group and the workspace group (for dashboards).</summary>
public sealed class SignalRExecutionEventSink(IHubContext<GatewayHub> hub) : IExecutionEventSink
{
    public async Task PublishAsync(Guid workspaceId, Guid sessionId, Guid executionId, string type, object data, CancellationToken ct)
    {
        var payload = new { workspaceId, sessionId, executionId, type, data, at = DateTimeOffset.UtcNow };
        await hub.Clients.Group(GatewayHub.GroupName(workspaceId, sessionId)).SendAsync("event", payload, ct);
        await hub.Clients.Group(GatewayHub.WorkspaceGroup(workspaceId)).SendAsync("event", payload, ct);
    }
}
