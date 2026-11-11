using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Channels;

/// <summary>Delivers queued outbound <see cref="ChannelMessage"/> rows through the matching channel adapter, with retry and dead-lettering.</summary>
public sealed class ChannelOutboxWorker(IServiceScopeFactory scopes, IEnumerable<IChannelAdapter> adapters, ILogger<ChannelOutboxWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IChannelAdapter> _byKind = adapters.ToDictionary(a => a.Kind);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Channel outbox poll failed"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DeliverBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenant.SetSystem();
        var now = DateTimeOffset.UtcNow;
        var pending = await db.ChannelMessages.IgnoreQueryFilters()
            .Where(m => m.Direction == "out" && m.Status == "pending" && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .Take(20).ToListAsync(ct);

        foreach (var message in pending)
        {
            var account = await db.ChannelAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == message.ChannelAccountId, ct);
            var channel = account is null ? null : await db.Channels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == account.ChannelId, ct);
            if (account is null || channel is null || !_byKind.TryGetValue(channel.Kind, out var adapter))
            {
                message.Status = "failed"; message.Reason = "no adapter for this channel"; continue;
            }
            try
            {
                var context = new ChannelAccountContext
                {
                    AccountId = account.Id, WorkspaceId = account.WorkspaceId, WebhookUrl = "", Credentials = new Dictionary<string, string>(),
                    Settings = account.Settings, Http = new HttpClient(),
                };
                var result = await adapter.SendAsync(context, new OutboundMessage(message.ExternalChatId, message.Text), ct);
                message.Status = "delivered";
                message.ExternalMessageId = result.ExternalMessageId;
            }
            catch (ChannelException ex)
            {
                message.Attempts++;
                message.Reason = ex.Message;
                message.Status = message.Attempts >= 5 ? "dead" : "pending";
                message.NextAttemptAt = ex.RetryAfter is { } r ? DateTimeOffset.UtcNow.Add(r) : DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, message.Attempts) * 5);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
