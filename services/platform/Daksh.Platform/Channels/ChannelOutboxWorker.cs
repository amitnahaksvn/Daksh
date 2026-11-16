using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Channels;

/// <summary>
/// Pending MongoDB rebuild: previously delivered queued outbound <see cref="Domain.ChannelMessage"/> rows
/// through the matching channel adapter against PlatformDbContext. Idles until that's rebuilt.
/// </summary>
public sealed class ChannelOutboxWorker(IEnumerable<IChannelAdapter> adapters, ILogger<ChannelOutboxWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IChannelAdapter> _byKind = adapters.ToDictionary(a => a.Kind);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning("Channel outbox worker is idle: channel message persistence is pending the MongoDB rebuild.");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }
}
