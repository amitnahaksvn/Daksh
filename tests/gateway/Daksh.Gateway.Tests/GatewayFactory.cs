using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Hosts the real Gateway in-process (JWT auth, SignalR hub, all REST modules, every hosted background
/// service: RuntimeWorker, JobRunnerWorker, ChannelOutboxWorker). Data persistence is pending the MongoDB
/// rebuild, so most REST endpoints currently return 501 (see Daksh.Gateway's Modules/*.cs) and the hosted
/// background workers idle; this factory just gives tests a running process to call.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:ConnectionString"] = "mongodb://localhost:27017",
                ["Mongo:Database"] = "daksh-gateway-test",
                ["Tools:WebSearch:Provider"] = "scripted",
                ["Scheduler:PollIntervalSeconds"] = "1",
            });
        });
    }
}

/// <summary>xUnit collection so every test in it shares one Gateway instance.</summary>
[CollectionDefinition(nameof(GatewayCollection))]
public sealed class GatewayCollection : ICollectionFixture<GatewayFactory>;
