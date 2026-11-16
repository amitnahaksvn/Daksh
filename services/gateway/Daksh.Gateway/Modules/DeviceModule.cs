using Daksh.Gateway.Auth;
using Daksh.Gateway.Hubs;
using Daksh.Platform.Identity;
using Microsoft.AspNetCore.SignalR;

namespace Daksh.Gateway.Modules;

public sealed record PairingInitiatedDto(Guid PairingId, string Code, DateTimeOffset ExpiresAt);
public sealed record ClaimPairingRequest(string Code, List<string>? Capabilities);
public sealed record DeviceCapabilityDto(string Name, bool Allowed, bool RequiresApproval);
public sealed record DeviceDto(Guid Id, string Name, string Platform, string Status, bool Online, DateTimeOffset? LastSeenAt, List<DeviceCapabilityDto> Capabilities);
public sealed record InvokeDeviceRequest(string Capability, string? ArgumentsJson);

/// <summary>
/// Gateway side of device pairing (DV-001/DV-002/DV-004). Pending MongoDB rebuild: pairing, approval, revocation
/// and invoke all previously persisted via PlatformDbContext (DevicePairings/Devices/DeviceCapabilities/DeviceEvents).
/// </summary>
public static class DeviceModule
{
    public static void MapDevices(this IEndpointRouteBuilder app)
    {
        var unavailable = () => Results.Problem(statusCode: 501, title: "Device pairing is unavailable: pending MongoDB rebuild.");

        var pairing = app.MapGroup("/api/v1/devices/pairing").WithTags("Devices");
        pairing.MapPost("/initiate", () => unavailable()).RequireAuthorization().RequirePermission(Permissions.DevicesManage);
        pairing.MapPost("/claim", (ClaimPairingRequest req) => unavailable());

        var devices = app.MapGroup("/api/v1/devices").RequireAuthorization().WithTags("Devices");
        devices.MapGet("/", () => unavailable()).RequirePermission(Permissions.DevicesRead);
        devices.MapPost("/{id:guid}/approve", (Guid id, HttpContext http) => unavailable()).RequirePermission(Permissions.DevicesManage);
        devices.MapPost("/{id:guid}/revoke", (Guid id) => unavailable()).RequirePermission(Permissions.DevicesManage);
        devices.MapPost("/{id:guid}/invoke", (Guid id, InvokeDeviceRequest req, IHubContext<NodeHub> hub) => unavailable())
            .RequirePermission(Permissions.DevicesManage);
    }
}
