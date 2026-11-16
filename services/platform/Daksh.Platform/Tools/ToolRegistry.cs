using Daksh.Platform.Domain;
using Daksh.Platform.Tools;

namespace Daksh.Platform.Tools;

/// <summary>Holds every registered tool (core and plugin) and mirrors their descriptors into the database for the UI/audit.</summary>
public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Descriptor.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> All => _byName.Values;
    public ITool? Find(string name) => _byName.GetValueOrDefault(name);

    // Pending MongoDB rebuild: previously mirrored tool descriptors into PlatformDbContext.ToolDefinitions.
    public Task SyncDefinitionsAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
