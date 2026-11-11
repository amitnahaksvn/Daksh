using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Runtime;

/// <summary>
/// Claims queued or lease-expired executions with a time-boxed lease and runs them. Because the lease (not an
/// in-memory flag) is the source of truth, a Gateway restart simply lets another worker pick the row up once the
/// old lease expires: no execution is lost, and the resumed run continues from the last message already saved.
/// </summary>
public sealed class RuntimeWorker(IServiceScopeFactory scopes, IOptions<RuntimeOptions> options, IClock clock, ILogger<RuntimeWorker> logger)
    : BackgroundService
{
    public static readonly string WorkerId = $"worker-{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Runtime worker {WorkerId} started", WorkerId);
        using var limiter = new SemaphoreSlim(Math.Max(1, options.Value.WorkerConcurrency));
        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try { claimed = await ClaimAndRunBatchAsync(limiter, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Runtime worker poll failed"); claimed = 0; }

            if (claimed == 0)
            {
                try { await Task.Delay(options.Value.PollIntervalMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        logger.LogInformation("Runtime worker {WorkerId} stopped", WorkerId);
    }

    private async Task<int> ClaimAndRunBatchAsync(SemaphoreSlim limiter, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenant.SetSystem();
        var now = clock.UtcNow;

        var candidates = await db.Executions.IgnoreQueryFilters()
            .Where(e => (e.Status == ExecutionStatus.Queued && e.CancelRequested == false)
                     || (e.Status == ExecutionStatus.Running && (e.LeaseUntil == null || e.LeaseUntil < now)))
            .OrderBy(e => e.CreatedAt).Take(options.Value.WorkerConcurrency * 2).Select(e => e.Id).ToListAsync(ct);

        var claimedCount = 0;
        foreach (var id in candidates)
        {
            if (!await limiter.WaitAsync(0, ct)) break;
            _ = RunOneAsync(id, limiter, ct);
            claimedCount++;
        }
        return claimedCount;
    }

    private async Task RunOneAsync(Guid executionId, SemaphoreSlim limiter, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenant.SetSystem();
            var execution = await db.Executions.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == executionId, ct);
            if (execution is null) return;

            // Optimistic claim: only proceed if we win the race to set status/lease.
            var expectedVersion = execution.Version;
            execution.Status = ExecutionStatus.Running;
            execution.LeaseUntil = clock.UtcNow.AddSeconds(options.Value.LeaseSeconds);
            execution.WorkerId = WorkerId;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return; }

            var workspaceId = execution.WorkspaceId;
            db.Tenant.SetWorkspaceAsSystem(workspaceId);

            using var leaseRenewal = new CancellationTokenSource();
            var renewTask = RenewLeaseLoopAsync(scope.ServiceProvider, executionId, leaseRenewal.Token);
            try
            {
                var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.DefaultRunTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await engine.RunAsync(executionId, linked.Token);
            }
            catch (OperationCanceledException) { /* lease released inside the engine */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Execution {ExecutionId} failed unexpectedly", executionId);
                var failDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var row = await failDb.Executions.FindAsync([executionId], ct);
                if (row is not null) { row.Status = ExecutionStatus.Failed; row.Error = "Internal error."; row.LeaseUntil = null; await failDb.SaveChangesAsync(ct); }
            }
            finally { await leaseRenewal.CancelAsync(); try { await renewTask; } catch (OperationCanceledException) { } }
        }
        finally { limiter.Release(); }
    }

    private async Task RenewLeaseLoopAsync(IServiceProvider services, Guid executionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseSeconds / 3)), ct);
                using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Tenant.SetSystem();
                var row = await db.Executions.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == executionId, ct);
                if (row is null || row.Status != ExecutionStatus.Running) return;
                row.LeaseUntil = clock.UtcNow.AddSeconds(options.Value.LeaseSeconds);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
