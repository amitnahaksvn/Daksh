using Daksh.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.TestSupport;

/// <summary>Hosts the real API in-process with an isolated in-memory database and dev authentication.</summary>
public sealed class DakshApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"daksh-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting applies before Program.cs reads configuration (ConfigureAppConfiguration does not).
        builder.UseSetting("Auth:Mode", "Development");
        builder.UseSetting("ConnectionStrings:Daksh", "Host=unused");
        builder.ConfigureServices(s =>
        {
            var existing = s.Where(d => d.ServiceType == typeof(DbContextOptions<DakshDbContext>)
                             || d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration")).ToList();
            foreach (var d in existing) s.Remove(d);
            s.AddDbContext<DakshDbContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }

    public HttpClient ClientFor(string user)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user);
        return c;
    }
}
