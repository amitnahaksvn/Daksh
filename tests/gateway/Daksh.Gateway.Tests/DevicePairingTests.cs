using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Daksh.Gateway.Tests;

/// <summary>
/// DV-001/DV-002/DV-004: the full device-pairing flow (initiate -> claim -> requires approval -> approve ->
/// paired), revocation actually blocking further access, and a real SignalR invoke over the node hub.
///
/// This drives the *protocol* the reference node (<c>services/node/Daksh.Node.Desktop/Program.cs</c>) speaks,
/// using a minimal test client built from reading that file rather than launching the actual node
/// executable. Three reasons: (1) the node is an interactive console app meant to run forever
/// (<c>await Task.Delay(Timeout.Infinite)</c>) with no clean exit or readiness signal a test could wait on
/// without an arbitrary sleep; (2) this worktree's absolute path contains spaces and non-ASCII characters,
/// which is a real source of quoting/encoding breakage for spawning a child `dotnet` process and is exactly
/// the kind of flakiness a CI run should not depend on; (3) the task description explicitly sanctions this
/// fallback. The test client below reproduces the node's HTTP claim call and hub connection byte-for-byte in
/// shape: same request body (<c>{ code, capabilities }</c>), same response field read (<c>token</c> out of a
/// flat string map), same hub URL (<c>/hubs/node</c>), same <c>AccessTokenProvider</c> wiring, and the same
/// "invoke" client method signature (<c>connection.On&lt;string, string&gt;("invoke", ...)</c>).
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class DevicePairingTests(GatewayFactory factory)
{
    private async Task<(string Token, string Workspace)> RegisterWorkspaceAsync(ApiClient api, string label)
    {
        var (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/auth/register", body: new
        {
            email = $"{label}-{Guid.NewGuid():N}@example.com", password = "Sup3rSecret!",
            displayName = label, workspaceName = $"{label} workspace",
        });
        b = ApiClient.Expect($"register ({label})", s, b);
        return (b.GetStringProp("accessToken"), b.GetGuidProp("workspaceId").ToString());
    }

    /// <summary>
    /// Starts a node-hub connection that the server is expected to reject (unapproved or revoked device).
    /// Throwing <c>HubException</c> from <see cref="Daksh.Gateway.Hubs.NodeHub.OnConnectedAsync"/> happens
    /// after the long-polling transport's own handshake has already succeeded, so the client's
    /// <c>StartAsync()</c> call itself does not throw - the rejection instead surfaces as the connection
    /// closing right after. This waits for that close rather than asserting on <c>StartAsync</c>.
    /// </summary>
    private static async Task StartAndExpectRejectionAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask; };
        await connection.StartAsync();
        var winner = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(closed.Task, winner);
    }

    private HubConnection BuildNodeConnection(string deviceToken) =>
        new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}hubs/node", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                // TestServer has no real socket to upgrade, so (as with any WebApplicationFactory + SignalR
                // integration test) the transport is pinned to long polling, which runs entirely over ordinary
                // HTTP requests through the same in-memory handler.
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(deviceToken);
            })
            .Build();

    [Fact]
    public async Task Full_pairing_flow_requires_approval_then_connects_and_invokes_then_revoke_blocks_it()
    {
        var http = factory.CreateClient();
        var api = new ApiClient(http);
        var (userToken, ws) = await RegisterWorkspaceAsync(api, "device-owner");

        // 1. Initiate: a user generates a short-lived pairing code tied to their workspace.
        var (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/devices/pairing/initiate", userToken, ws);
        b = ApiClient.Expect("1. initiate pairing", s, b);
        var code = b.GetStringProp("code");
        Assert.False(string.IsNullOrWhiteSpace(code));

        // 2. Claim: the node redeems the code - exactly the request Daksh.Node.Desktop/Program.cs sends,
        // with no auth of its own, and gets a device token back.
        var claimResponse = await http.PostAsJsonAsync("/api/v1/devices/pairing/claim",
            new { code, capabilities = new[] { "system.info", "notify.show" } });
        Assert.True(claimResponse.IsSuccessStatusCode, $"claim failed: {claimResponse.StatusCode} {await claimResponse.Content.ReadAsStringAsync()}");
        var claimed = await claimResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var deviceToken = claimed!["token"];
        var deviceId = Guid.Parse(claimed["deviceId"]);
        Assert.False(string.IsNullOrWhiteSpace(deviceToken));

        // 3. Not auto-approved: the device is listed but pending, and a connection attempt with its freshly
        // claimed token is rejected by the node hub.
        (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/devices", userToken, ws);
        var devices = ApiClient.Expect("3a. list devices", s, b);
        var listed = devices.EnumerateArray().First(d => d.GetGuidProp("id") == deviceId);
        Assert.Equal("pending_approval", listed.GetStringProp("status"));
        Assert.All(listed.GetProperty("capabilities").EnumerateArray(), c => Assert.False(c.GetProperty("allowed").GetBoolean()));

        await using (var tooEarly = BuildNodeConnection(deviceToken))
        {
            await StartAndExpectRejectionAsync(tooEarly);
        }

        // Invoking before approval must also fail even if a connection had somehow been made.
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/devices/{deviceId}/invoke", userToken, ws, new { capability = "system.info" });
        Assert.Equal(409, s);

        // 4. Approve: an explicit user action, not automatic.
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/devices/{deviceId}/approve", userToken, ws);
        b = ApiClient.Expect("4. approve device", s, b);
        Assert.Equal("active", b.GetStringProp("status"));
        Assert.All(b.GetProperty("capabilities").EnumerateArray(), c => Assert.True(c.GetProperty("allowed").GetBoolean()));

        // 5. Now paired: the same token the node already holds (no second claim round-trip, matching the
        // node's automatic-reconnect behavior) connects successfully to the node hub.
        var invoked = new TaskCompletionSource<(string Capability, string ArgumentsJson)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = BuildNodeConnection(deviceToken);
        connection.On<string, string>("invoke", (capability, argumentsJson) =>
        {
            invoked.TrySetResult((capability, argumentsJson));
        });
        await connection.StartAsync();
        try
        {
            (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/devices", userToken, ws);
            var afterConnect = ApiClient.Expect("5a. list devices after connect", s, b)
                .EnumerateArray().First(d => d.GetGuidProp("id") == deviceId);
            Assert.True(afterConnect.GetProperty("online").GetBoolean());

            // 6. Node hub invoke test: the Gateway's REST invoke endpoint reaches the connected device over
            // SignalR and the exact same client-side handler shape the reference node registers actually runs.
            (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/devices/{deviceId}/invoke", userToken, ws,
                new { capability = "system.info", argumentsJson = "{}" });
            Assert.Equal(202, s);

            var completed = await Task.WhenAny(invoked.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(invoked.Task, completed);
            var (gotCapability, _) = await invoked.Task;
            Assert.Equal("system.info", gotCapability);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }

        // 7. Revoke: further invokes are rejected ...
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/devices/{deviceId}/revoke", userToken, ws);
        ApiClient.Expect("7. revoke device", s, b);

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/devices/{deviceId}/invoke", userToken, ws, new { capability = "system.info" });
        Assert.Equal(409, s);

        // ... and a fresh connection attempt with the same (never-rotated) device token is rejected too.
        await using var afterRevoke = BuildNodeConnection(deviceToken);
        await StartAndExpectRejectionAsync(afterRevoke);

        (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/devices", userToken, ws);
        var finalState = ApiClient.Expect("7b. list devices after revoke", s, b)
            .EnumerateArray().First(d => d.GetGuidProp("id") == deviceId);
        Assert.Equal("revoked", finalState.GetStringProp("status"));
    }

    [Fact]
    public async Task Claiming_an_unknown_or_expired_code_is_rejected()
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/devices/pairing/claim",
            new { code = "NOT-A-REAL-CODE", capabilities = Array.Empty<string>() });
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Claiming_the_same_code_twice_only_succeeds_once()
    {
        var http = factory.CreateClient();
        var api = new ApiClient(http);
        var (userToken, ws) = await RegisterWorkspaceAsync(api, "double-claim-owner");

        var (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/devices/pairing/initiate", userToken, ws);
        var code = ApiClient.Expect("initiate", s, b).GetStringProp("code");

        var first = await http.PostAsJsonAsync("/api/v1/devices/pairing/claim", new { code, capabilities = Array.Empty<string>() });
        Assert.True(first.IsSuccessStatusCode);

        var second = await http.PostAsJsonAsync("/api/v1/devices/pairing/claim", new { code, capabilities = Array.Empty<string>() });
        Assert.False(second.IsSuccessStatusCode);
    }
}
