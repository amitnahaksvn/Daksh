using System.Text.Json;
using Daksh.Gateway.Auth;
using Daksh.Gateway.Hubs;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Identity;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Gateway.Modules;

public sealed record PairingInitiatedDto(Guid PairingId, string Code, DateTimeOffset ExpiresAt);
/// <summary>Body the reference node actually sends (see Daksh.Node.Desktop/Program.cs): just the code it was
/// given and the capability names it offers. There is no device name/platform field in the node's request, so
/// those are filled in with placeholders here and are expected to be refined by a future node version.</summary>
public sealed record ClaimPairingRequest(string Code, List<string>? Capabilities);
public sealed record DeviceCapabilityDto(string Name, bool Allowed, bool RequiresApproval);
public sealed record DeviceDto(Guid Id, string Name, string Platform, string Status, bool Online, DateTimeOffset? LastSeenAt, List<DeviceCapabilityDto> Capabilities);
public sealed record InvokeDeviceRequest(string Capability, string? ArgumentsJson);

/// <summary>
/// Gateway side of device pairing (DV-001/DV-002/DV-004). The reference node (<c>Daksh.Node.Desktop</c>)
/// already speaks this exact protocol - see its <c>Program.cs</c> - so the request/response shapes here are
/// built to match what it sends and expects, not a new design: it POSTs <c>{ code, capabilities }</c> to
/// <c>/pairing/claim</c> and reads a <c>token</c> field back out of a flat string-to-string JSON object, then
/// connects to <c>/hubs/node</c> (mapped to <see cref="NodeHub"/>) using that token.
///
/// Pairing is never auto-approved: claiming a code creates a <see cref="Device"/> in "pending_approval" and
/// its requested capabilities as unallowed; a user with devices.manage must call <c>/approve</c> before the
/// node's token will be accepted by <see cref="NodeHub"/>. The node already holds its token at claim time and
/// typically reconnects automatically (<c>.WithAutomaticReconnect()</c>), so a connection attempt made before
/// approval simply fails and a later retry (after approval) succeeds, with no second claim round-trip needed.
/// </summary>
public static class DeviceModule
{
    public static void MapDevices(this IEndpointRouteBuilder app)
    {
        var pairing = app.MapGroup("/api/v1/devices/pairing").WithTags("Devices");

        pairing.MapPost("/initiate", async (PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var code = Tokens.Code();
            var record = new DevicePairing
            {
                WorkspaceId = db.Tenant.RequireWorkspace(),
                CodeHash = Tokens.Hash(code),
                Status = "pending",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                CreatedByUserId = WorkspaceAndProviderModule.CurrentUserId(http),
            };
            db.DevicePairings.Add(record);
            await db.SaveChangesAsync(ct);
            // The plaintext code is only ever handed back here, at the moment of creation; only its hash is
            // ever stored, the same pattern used for refresh tokens and password reset tokens.
            return Results.Ok(new PairingInitiatedDto(record.Id, code, record.ExpiresAt));
        }).RequireAuthorization().RequirePermission(Permissions.DevicesManage);

        // Called directly by the node - no user is signed in yet, so this endpoint carries no auth of its
        // own; the pairing code itself (high-entropy, short-lived, hashed at rest, single-use) is the
        // credential. It must look up a DevicePairing across every workspace by code hash alone, which is why
        // it switches the DbContext to the system scope, the same way background workers do.
        pairing.MapPost("/claim", async (ClaimPairingRequest req, PlatformDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.Problem(statusCode: 400, title: "code is required.");

            db.Tenant.SetSystem();
            var hash = Tokens.Hash(req.Code.Trim());
            var pending = await db.DevicePairings.FirstOrDefaultAsync(p => p.CodeHash == hash, ct);
            if (pending is null || pending.Status != "pending" || pending.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Problem(statusCode: 400, title: "Pairing code is invalid, already used, or expired.");

            var token = Tokens.Generate();
            var capabilities = req.Capabilities ?? [];
            var device = new Device
            {
                WorkspaceId = pending.WorkspaceId,
                Name = "Unnamed device",
                Platform = "unknown",
                OwnerUserId = pending.CreatedByUserId ?? Guid.Empty,
                Status = "pending_approval",
                TokenHash = Tokens.Hash(token),
            };
            db.Devices.Add(device);
            foreach (var capability in capabilities)
            {
                db.DeviceCapabilities.Add(new DeviceCapability
                {
                    WorkspaceId = pending.WorkspaceId, DeviceId = device.Id, Name = capability,
                    Allowed = false, RequiresApproval = true,
                });
            }

            pending.Status = "claimed";
            pending.DeviceId = device.Id;
            pending.RequestedCapabilities = capabilities;
            pending.ClaimSecretHash = device.TokenHash;
            await db.SaveChangesAsync(ct);

            // A flat string->string map, exactly what Daksh.Node.Desktop deserializes
            // (`ReadFromJsonAsync<Dictionary<string, string>>()`) and reads "token" out of.
            return Results.Ok(new Dictionary<string, string> { ["token"] = token, ["deviceId"] = device.Id.ToString() });
        });

        var devices = app.MapGroup("/api/v1/devices").RequireAuthorization().WithTags("Devices");

        devices.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var items = await db.Devices.Include(d => d.Capabilities).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
            return Results.Ok(items.Select(ToDto));
        }).RequirePermission(Permissions.DevicesRead);

        devices.MapPost("/{id:guid}/approve", async (Guid id, PlatformDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var device = await db.Devices.Include(d => d.Capabilities).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (device is null) return Results.NotFound();
            if (device.Status == "revoked") return Results.Problem(statusCode: 409, title: "Device was revoked; pair it again to re-approve.");
            if (device.Status != "active")
            {
                device.Status = "active";
                foreach (var capability in device.Capabilities) capability.Allowed = true;

                var record = await db.DevicePairings.FirstOrDefaultAsync(p => p.DeviceId == device.Id, ct);
                if (record is not null)
                {
                    record.Status = "approved";
                    record.ApprovedByUserId = WorkspaceAndProviderModule.CurrentUserId(http);
                }
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(device));
        }).RequirePermission(Permissions.DevicesManage);

        devices.MapPost("/{id:guid}/revoke", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.FindAsync([id], ct);
            if (device is null) return Results.NotFound();
            device.Status = "revoked";
            device.RevokedAt = DateTimeOffset.UtcNow;
            device.Online = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { device.Id, device.Status });
        }).RequirePermission(Permissions.DevicesManage);

        // Drives the node hub's "invoke" client method (DV-004); not itself a DV-001 acceptance criterion,
        // but needed to actually exercise the node hub connection end to end once a device is approved, and
        // to prove revocation blocks it afterward.
        devices.MapPost("/{id:guid}/invoke", async (Guid id, InvokeDeviceRequest req, PlatformDbContext db, IHubContext<NodeHub> hub, CancellationToken ct) =>
        {
            var device = await db.Devices.FindAsync([id], ct);
            if (device is null) return Results.NotFound();
            if (device.Status != "active") return Results.Problem(statusCode: 409, title: "Device is not active (pending approval or revoked).");
            if (!device.Online) return Results.Problem(statusCode: 409, title: "Device is not currently connected.");

            db.DeviceEvents.Add(new DeviceEvent
            {
                WorkspaceId = device.WorkspaceId, DeviceId = device.Id, Type = "invoke",
                Data = JsonSerializer.Serialize(new { req.Capability, req.ArgumentsJson }),
            });
            await db.SaveChangesAsync(ct);

            await hub.Clients.Group(NodeHub.GroupName(device.Id)).SendAsync("invoke", req.Capability, req.ArgumentsJson ?? "{}", ct);
            return Results.Accepted();
        }).RequirePermission(Permissions.DevicesManage);
    }

    private static DeviceDto ToDto(Device d) => new(d.Id, d.Name, d.Platform, d.Status, d.Online, d.LastSeenAt,
        d.Capabilities.Select(c => new DeviceCapabilityDto(c.Name, c.Allowed, c.RequiresApproval)).ToList());
}
