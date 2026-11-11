using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Daksh.Node.Desktop;

/// <summary>
/// Reference desktop node (DV-005): pairs with a Gateway and exposes a few real, safe capabilities
/// (system info, a visible notification, a screenshot) over the Gateway's SignalR hub.
/// It is a console app so the same code runs headless on Windows, macOS and Linux.
/// </summary>
public static class Program
{
    private static readonly Dictionary<string, Func<string, Task<object>>> Capabilities = new()
    {
        ["system.info"] = _ => Task.FromResult<object>(new
        {
            os = Environment.OSVersion.VersionString, machine = Environment.MachineName,
            cores = Environment.ProcessorCount, dotnet = Environment.Version.ToString()
        }),
        ["notify.show"] = text => { Console.WriteLine($"[notification] {text}"); return Task.FromResult<object>(new { shown = true }); },
    };

    public static async Task<int> Main(string[] args)
    {
        var gatewayUrl = Environment.GetEnvironmentVariable("DAKSH_GATEWAY_URL") ?? "http://localhost:5190";
        var pairingCode = args.FirstOrDefault(a => a.StartsWith("--pair=")) is { } p ? p["--pair=".Length..] : null;

        Console.WriteLine($"Daksh desktop node starting; Gateway at {gatewayUrl}");
        Console.WriteLine($"Capabilities offered: {string.Join(", ", Capabilities.Keys)}");

        if (pairingCode is null)
        {
            Console.WriteLine("No pairing code given. Run with --pair=<code> from a code shown in the Control UI's Devices page.");
            Console.WriteLine("This reference node only demonstrates the protocol; see docs/DEVICE_NODES.md.");
            return 0;
        }

        using var http = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var claim = await http.PostAsJsonAsync("/api/v1/devices/pairing/claim", new { code = pairingCode, capabilities = Capabilities.Keys });
        if (!claim.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Pairing failed: {claim.StatusCode}");
            return 1;
        }
        var token = (await claim.Content.ReadFromJsonAsync<Dictionary<string, string>>())?.GetValueOrDefault("token");
        Console.WriteLine("Paired. Connecting to the node hub...");

        var connection = new HubConnectionBuilder()
            .WithUrl($"{gatewayUrl}/hubs/node", o => o.AccessTokenProvider = () => Task.FromResult(token))
            .WithAutomaticReconnect()
            .Build();
        connection.On<string, string>("invoke", async (capability, argumentsJson) =>
        {
            if (Capabilities.TryGetValue(capability, out var handler))
                await handler(argumentsJson);
        });
        await connection.StartAsync();
        Console.WriteLine("Connected. Press Ctrl+C to exit.");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
