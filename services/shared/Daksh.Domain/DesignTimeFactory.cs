using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daksh.Domain;

/// <summary>Used by `dotnet ef` only; no database connection is made when generating migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DakshDbContext>
{
    public DakshDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Daksh")
                 ?? "Host=localhost;Database=daksh;Username=daksh;Password=change-me";
        var options = new DbContextOptionsBuilder<DakshDbContext>().UseNpgsql(cs).Options;
        return new DakshDbContext(options);
    }
}
