using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Daksh.Cli;

/// <summary>
/// Administration CLI for a running Gateway. Talks to its REST API, the same surface the Control UI uses.
/// `daksh <command> [args]`. Set DAKSH_GATEWAY_URL and DAKSH_ACCESS_TOKEN to point it at a Gateway.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var baseUrl = Environment.GetEnvironmentVariable("DAKSH_GATEWAY_URL") ?? "http://localhost:5190";
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        if (Environment.GetEnvironmentVariable("DAKSH_ACCESS_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (args.Length == 0) { PrintUsage(); return 1; }

        try
        {
            switch (args[0])
            {
                case "gateway" when args.ElementAtOrDefault(1) == "status":
                    var info = await http.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/system/info");
                    Console.WriteLine(string.Join("\n", info?.Select(kv => $"{kv.Key}: {kv.Value}") ?? []));
                    return 0;
                case "health":
                    var ready = await http.GetAsync("/health/ready");
                    Console.WriteLine($"ready: {ready.IsSuccessStatusCode}");
                    return ready.IsSuccessStatusCode ? 0 : 1;
                case "agents" when args.ElementAtOrDefault(1) == "list":
                    var agents = await http.GetStringAsync("/api/v1/agents");
                    Console.WriteLine(agents);
                    return 0;
                case "automations" when args.ElementAtOrDefault(1) == "list":
                    Console.WriteLine(await http.GetStringAsync("/api/v1/automations"));
                    return 0;
                case "usage":
                    Console.WriteLine(await http.GetStringAsync("/api/v1/usage/summary"));
                    return 0;
                case "--help" or "-h" or "help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage();
                    return 1;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not reach the Gateway at {baseUrl}: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        daksh - Daksh agent platform CLI

        Usage: daksh <command> [subcommand]

        Commands:
          gateway status      Show Gateway name, version and protocol version
          health               Check Gateway readiness
          agents list          List agents in the current workspace
          automations list     List automations in the current workspace
          usage                Show usage and cost summary

        Environment:
          DAKSH_GATEWAY_URL     Gateway base URL (default http://localhost:5190)
          DAKSH_ACCESS_TOKEN    Bearer token (and X-Workspace-Id via DAKSH_WORKSPACE_ID)
        """);
}
