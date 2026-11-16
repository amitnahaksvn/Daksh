using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Daksh.TestSupport;

/// <summary>Hosts the real API in-process with dev authentication. Data persistence is pending the MongoDB
/// rebuild, so endpoints backed by it return 501 for now (see AssistantModule/TasksModule/PlatformModules).</summary>
public sealed class DakshApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting applies before Program.cs reads configuration (ConfigureAppConfiguration does not).
        builder.UseSetting("Auth:Mode", "Development");
        builder.UseSetting("Mongo:ConnectionString", "mongodb://localhost:27017");
        builder.UseSetting("Mongo:Database", "daksh-test");
    }

    public HttpClient ClientFor(string user)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user);
        return c;
    }
}
