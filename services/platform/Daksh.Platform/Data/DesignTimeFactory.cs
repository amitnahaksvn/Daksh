using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Daksh.Platform.Data;

/// <summary>Used by `dotnet ef` only. No connection is opened when a migration is generated.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Platform") ?? "Host=localhost;Database=daksh_platform;Username=daksh;Password=change-me";
        var tenant = new TenantContext();
        tenant.SetSystem();
        return new PlatformDbContext(Configure(new DbContextOptionsBuilder<PlatformDbContext>(), cs).Options, tenant);
    }

    public static DbContextOptionsBuilder<PlatformDbContext> Configure(DbContextOptionsBuilder<PlatformDbContext> b, string connectionString) =>
        (DbContextOptionsBuilder<PlatformDbContext>)Configure((DbContextOptionsBuilder)b, connectionString);

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder b, string connectionString) =>
        b.UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", PlatformDbContext.Schema)
            .MigrationsAssembly(typeof(PlatformDbContext).Assembly.GetName().Name));
}
