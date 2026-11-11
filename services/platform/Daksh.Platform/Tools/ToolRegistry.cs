using Daksh.Platform.Domain;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Platform.Tools;

/// <summary>Holds every registered tool (core and plugin) and mirrors their descriptors into the database for the UI/audit.</summary>
public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Descriptor.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> All => _byName.Values;
    public ITool? Find(string name) => _byName.GetValueOrDefault(name);

    public async Task SyncDefinitionsAsync(Data.PlatformDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var existing = await db.ToolDefinitions.ToDictionaryAsync(d => d.Name, ct);
        foreach (var tool in All)
        {
            var d = tool.Descriptor;
            var unavailable = tool.Unavailable(services);
            if (!existing.TryGetValue(d.Name, out var row))
            {
                row = new ToolDefinition { Name = d.Name };
                db.ToolDefinitions.Add(row);
            }
            row.Version = d.Version; row.Description = d.Description; row.Group = d.Group;
            row.Risk = d.Risk.ToString().ToLowerInvariant(); row.Source = d.Source;
            row.InputSchema = d.InputSchema.GetRawText();
            row.OutputSchema = d.OutputSchema?.GetRawText();
            row.Permissions = (d.Permissions ?? []).ToList();
            row.TimeoutSeconds = d.TimeoutSeconds; row.MaxRetries = d.MaxRetries;
            row.Available = unavailable is null; row.UnavailableReason = unavailable;
        }
        await db.SaveChangesAsync(ct);
    }
}
