using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>
/// Initial and incremental synchronisation (Gmail history IDs), push-notification handling and watch renewal.
/// Idempotent: messages are keyed by (account, Gmail message id), so re-running or duplicate pushes change nothing.
/// </summary>
public sealed class GmailSyncEngine(GmailServices svc, GmailPipeline pipeline)
{
    private const int MaxMessagesPerRun = 150;
    private const int Concurrency = 4;

    /// <summary>Runs one sync pass. Returns the run record. Never throws for per-message failures.</summary>
    public async Task<GmailSyncRun> SyncAsync(string userId, string accountId, string trigger, CancellationToken ct)
    {
        var states = svc.Db.GetCollection<GmailSyncState>("gmailSyncState");
        var started = DateTime.UtcNow;
        var run = new GmailSyncRun { UserId = userId, AccountId = accountId, Trigger = trigger, StartedUtc = started };

        // Claim the account: only one sync at a time, and a crashed run (stale > 10 min) can be taken over.
        await states.UpdateOneAsync(s => s.AccountId == accountId, Builders<GmailSyncState>.Update.SetOnInsert(s => s.UserId, userId).SetOnInsert(s => s.Status, "Idle"),
            new UpdateOptions { IsUpsert = true }, ct);
        var stale = started.AddMinutes(-10);
        var claimed = await states.FindOneAndUpdateAsync(
            s => s.AccountId == accountId && s.UserId == userId && (s.Status != "Synchronising" || s.LastAttemptUtc < stale),
            Builders<GmailSyncState>.Update.Set(s => s.Status, "Synchronising").Set(s => s.LastAttemptUtc, started).Set(s => s.SyncRequested, false), cancellationToken: ct);
        if (claimed is null) { run.Status = "skipped"; run.Error = "A sync is already running."; return run; }

        var connection = await GmailIntegrationModule.ResolveConnectionAsync(svc.Db, userId, accountId, ct);
        if (connection is null) { await FinishAsync(states, run, accountId, "failed", "The account is not connected.", null, ct); return run; }
        var account = await GmailIntegrationModule.LoadAccountAsync(svc.Db, connection, ct);
        var cfg = await GmailIntegrationModule.LoadConfigAsync(svc.Db, userId, accountId, ct);

        try
        {
            if (!account.Enabled || !cfg.Sync.Enabled) { await FinishAsync(states, run, accountId, "skipped", "Synchronisation is turned off for this account.", claimed.HistoryId, ct, success: true); return run; }
            if (!GmailPermissionPolicy.IsAllowed(cfg.Permissions.Profile, cfg.Permissions.Overrides, GmailPermission.Read))
            { await FinishAsync(states, run, accountId, "skipped", "Reading mail isn't permitted for this account.", claimed.HistoryId, ct, success: true); return run; }

            string newHistoryId;
            try
            {
                if (!claimed.InitialSyncDone || string.IsNullOrEmpty(claimed.HistoryId))
                {
                    run.Kind = "initial";
                    newHistoryId = await FullSyncAsync(account, cfg, run, ct);
                }
                else
                {
                    run.Kind = "incremental";
                    newHistoryId = await IncrementalAsync(account, cfg, claimed.HistoryId!, run, ct);
                }
            }
            catch (GmailIntegrationException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound && run.Kind == "incremental")
            {
                // The stored history id is too old for Gmail to replay: start over (dedup keeps this safe).
                run.Kind = "full-resync";
                newHistoryId = await FullSyncAsync(account, cfg, run, ct);
            }

            await pipeline.EvaluateRepliesAsync(account, cfg, ct);
            await pipeline.SweepFollowUpsAsync(account, cfg, ct);
            await FinishAsync(states, run, accountId, run.Failed > 0 ? "partial" : "ok", run.Failed > 0 ? $"{run.Failed} message(s) could not be processed." : null, newHistoryId, ct, success: true, initialDone: true);
        }
        catch (GmailIntegrationException ex)
        {
            await FinishAsync(states, run, accountId, "failed", ex.Message, claimed.HistoryId, ct);
            var type = ex.StatusCode == 401 ? "connectionExpired" : ex.Code is "google_forbidden" or "scope_upgrade_required" ? "oauthPermissionIssue" : "syncFailure";
            await svc.Notify(cfg, type, $"{DateTime.UtcNow:yyyyMMddHH}", "Gmail sync problem", ex.Message, ct: CancellationToken.None);
        }
        catch (HttpRequestException ex)
        {
            svc.Log.LogWarning(ex, "Gmail sync could not reach Google for account {AccountId}", accountId);
            await FinishAsync(states, run, accountId, "failed", "Could not reach Google. Will retry.", claimed.HistoryId, ct);
        }
        catch (OperationCanceledException)
        {
            await states.UpdateOneAsync(s => s.AccountId == accountId, Builders<GmailSyncState>.Update.Set(s => s.Status, "Idle"), cancellationToken: CancellationToken.None);
            throw;
        }
        return run;
    }

    private async Task<string> FullSyncAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailSyncRun run, CancellationToken ct)
    {
        var userId = account.UserId; var accountId = account.ConnectionId;
        // Take the history id BEFORE listing so anything that changes during the listing is picked up by the next incremental pass.
        string historyId;
        using (var profile = await svc.Api(userId, accountId, "users/me/profile", ct: ct))
            historyId = profile.RootElement.GetProperty("historyId").GetString() ?? "";

        var query = cfg.Sync.InitialPeriodDays > 0 ? $"newer_than:{cfg.Sync.InitialPeriodDays}d" : "";
        var labels = cfg.Sync.Labels.Count > 0 ? cfg.Sync.Labels : [""];
        var ids = new List<string>();
        foreach (var label in labels)
        {
            string? page = null;
            do
            {
                var p = new Dictionary<string, string?> { ["maxResults"] = "100", ["includeSpamTrash"] = (cfg.Sync.IncludeSpam || cfg.Sync.IncludeTrash) ? "true" : "false" };
                if (query.Length > 0) p["q"] = query;
                if (label.Length > 0) p["labelIds"] = label;
                if (page is not null) p["pageToken"] = page;
                using var list = await svc.Api(userId, accountId, QueryHelpers.AddQueryString("users/me/messages", p), ct: ct);
                if (list.RootElement.TryGetProperty("messages", out var msgs))
                    ids.AddRange(msgs.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).Where(x => !ids.Contains(x)));
                page = list.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            } while (page is not null && ids.Count < MaxMessagesPerRun);
        }
        // Oldest first would be ideal; the list is newest first, so process in reverse for a stable follow-up timeline.
        ids.Reverse();
        await ProcessIdsAsync(account, cfg, ids.TakeLast(MaxMessagesPerRun).ToList(), run, ct);
        return historyId;
    }

    private async Task<string> IncrementalAsync(GmailAccountDoc account, GmailAccountConfig cfg, string startHistoryId, GmailSyncRun run, CancellationToken ct)
    {
        var userId = account.UserId; var accountId = account.ConnectionId;
        var toFetch = new HashSet<string>();
        var deleted = new HashSet<string>();
        var latest = startHistoryId;
        string? page = null;
        do
        {
            var p = new Dictionary<string, string?> { ["startHistoryId"] = startHistoryId, ["maxResults"] = "500" };
            if (page is not null) p["pageToken"] = page;
            using var history = await svc.Api(userId, accountId, QueryHelpers.AddQueryString("users/me/history", p), ct: ct);
            var root = history.RootElement;
            if (root.TryGetProperty("history", out var items))
                foreach (var h in items.EnumerateArray())
                {
                    CollectIds(h, "messagesAdded", toFetch);
                    CollectIds(h, "labelsAdded", toFetch);
                    CollectIds(h, "labelsRemoved", toFetch);
                    CollectIds(h, "messagesDeleted", deleted);
                }
            if (root.TryGetProperty("historyId", out var hid) && hid.GetString() is { Length: > 0 } hv) latest = hv;
            page = root.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page is not null);

        foreach (var id in deleted) toFetch.Remove(id);
        if (deleted.Count > 0)
            await svc.Db.GetCollection<GmailMessageDoc>("gmailMessages").DeleteManyAsync(d => d.AccountId == accountId && d.UserId == userId && deleted.Contains(d.MessageId), ct);
        await ProcessIdsAsync(account, cfg, toFetch.Take(MaxMessagesPerRun * 3).ToList(), run, ct);
        return latest;
    }

    private static void CollectIds(JsonElement history, string property, HashSet<string> into)
    {
        if (!history.TryGetProperty(property, out var arr)) return;
        foreach (var entry in arr.EnumerateArray())
            if (entry.TryGetProperty("message", out var m) && m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } v) into.Add(v);
    }

    /// <summary>Fetches and processes messages with bounded parallelism. A failing message is retried later and dead-lettered; it never stops the batch.</summary>
    private async Task ProcessIdsAsync(GmailAccountDoc account, GmailAccountConfig cfg, List<string> ids, GmailSyncRun run, CancellationToken ct)
    {
        run.Fetched += ids.Count;
        using var gate = new SemaphoreSlim(Concurrency);
        var dead = svc.Db.GetCollection<GmailDeadLetter>("gmailDeadLetters");
        await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var fetched = await svc.Fetch(account.UserId, account.ConnectionId, id, ct);
                if (await pipeline.ProcessAsync(account, cfg, fetched, run.Trigger, ct)) Interlocked.Increment(ref run.ProcessedBox);
                await dead.DeleteOneAsync(d => d.Key == $"{account.ConnectionId}:{id}", ct);
            }
            catch (GmailIntegrationException ex) when (ex.StatusCode == 404)
            {
                // Deleted between listing and fetching: nothing to do.
            }
            catch (GmailIntegrationException ex) when (ex.StatusCode is 401 or 403)
            {
                throw; // account-level problem: abort the pass so it's reported once, not once per message
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref run.FailedBox);
                svc.Log.LogWarning(ex, "Gmail message {MessageId} failed processing for account {AccountId}", id, account.ConnectionId);
                var key = $"{account.ConnectionId}:{id}";
                var entry = await dead.Find(d => d.Key == key).FirstOrDefaultAsync(ct);
                var attempts = (entry?.Attempts ?? 0) + 1;
                await dead.ReplaceOneAsync(d => d.Key == key, new GmailDeadLetter
                {
                    Key = key, UserId = account.UserId, AccountId = account.ConnectionId, MessageId = id, Attempts = attempts,
                    Error = ex.GetType().Name, Dead = attempts >= 5, LastAttemptUtc = DateTime.UtcNow,
                }, new ReplaceOptions { IsUpsert = true }, ct);
            }
            finally { gate.Release(); }
        }));
        run.Processed = run.ProcessedBox;
        run.Failed = run.FailedBox;
    }

    private async Task FinishAsync(IMongoCollection<GmailSyncState> states, GmailSyncRun run, string accountId, string status, string? error, string? historyId, CancellationToken ct,
        bool success = false, bool initialDone = false)
    {
        run.Status = status == "skipped" ? "ok" : status;
        run.Error = error;
        run.FinishedUtc = DateTime.UtcNow;
        var ok = success || status is "ok" or "partial";
        var update = Builders<GmailSyncState>.Update.Set(s => s.Status, ok ? "Idle" : "Failed").Set(s => s.LastAttemptUtc, run.StartedUtc);
        if (historyId is not null) update = update.Set(s => s.HistoryId, historyId);
        if (initialDone) update = update.Set(s => s.InitialSyncDone, true);
        update = ok
            ? update.Set(s => s.LastSuccessUtc, run.FinishedUtc).Set(s => s.LastError, error).Set(s => s.ConsecutiveFailures, 0).Inc(s => s.MessagesSynced, run.Processed)
            : update.Set(s => s.LastFailureUtc, run.FinishedUtc).Set(s => s.LastError, error).Inc(s => s.ConsecutiveFailures, 1);
        await states.UpdateOneAsync(s => s.AccountId == accountId, update, cancellationToken: CancellationToken.None);
        await svc.Db.GetCollection<GmailSyncRun>("gmailSyncHistory").InsertOneAsync(run, cancellationToken: CancellationToken.None);
    }

    // ── Gmail watch (Pub/Sub push) ────────────────────────────────────────────────────────────────────────────
    public bool WatchConfigured => !string.IsNullOrWhiteSpace(svc.Config["Gmail:PubSub:TopicName"]);

    /// <summary>Starts or renews the push subscription. Gmail watches expire after 7 days; the worker renews daily.</summary>
    public async Task<DateTime?> RenewWatchAsync(string userId, string accountId, CancellationToken ct)
    {
        var topic = svc.Config["Gmail:PubSub:TopicName"];
        if (string.IsNullOrWhiteSpace(topic)) return null;
        using var response = await svc.Api(userId, accountId, "users/me/watch", HttpMethod.Post,
            new { topicName = topic, labelIds = new[] { "INBOX", "SENT" }, labelFilterBehavior = "INCLUDE" }, ct);
        var expiration = response.RootElement.TryGetProperty("expiration", out var e) && long.TryParse(e.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
        var historyId = response.RootElement.TryGetProperty("historyId", out var h) ? h.GetString() : null;
        await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").UpdateOneAsync(s => s.AccountId == accountId,
            Builders<GmailSyncState>.Update.SetOnInsert(s => s.UserId, userId).Set(s => s.WatchExpiresUtc, expiration).Set(s => s.WatchHistoryId, historyId).Set(s => s.WatchRenewedUtc, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true }, ct);
        return expiration;
    }
}

/// <summary>Background worker: syncs accounts that are due, flagged by a push, or overdue for reconciliation, and renews watches.</summary>
public sealed class GmailSyncWorker(IServiceProvider services, ILogger<GmailSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Reconcile = TimeSpan.FromHours(6); // safety net when push is active

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // let the API finish starting
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Gmail sync worker tick failed"); }
            await Task.Delay(Tick, stoppingToken);
        }
    }

    /// <summary>Applies the retention settings: synced message info and attachment records older than the configured days are deleted.</summary>
    public static async Task PurgeExpiredAsync(IMongoDatabase db, GmailAccountDoc account, CancellationToken ct)
    {
        var cfg = await GmailIntegrationModule.LoadConfigAsync(db, account.UserId, account.ConnectionId, ct);
        var messageCutoff = DateTime.UtcNow.AddDays(-cfg.Privacy.RetentionDays);
        await db.GetCollection<GmailMessageDoc>("gmailMessages").DeleteManyAsync(m => m.UserId == account.UserId && m.AccountId == account.ConnectionId && m.SyncedAtUtc < messageCutoff, ct);
        var attachmentCutoff = DateTime.UtcNow.AddDays(-cfg.Attachments.RetentionDays);
        await db.GetCollection<GmailAttachmentDoc>("gmailAttachments").DeleteManyAsync(a => a.UserId == account.UserId && a.AccountId == account.ConnectionId && a.CreatedAtUtc < attachmentCutoff, ct);
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<GmailServices>();
        var engine = scope.ServiceProvider.GetRequiredService<GmailSyncEngine>();
        var accounts = await svc.Db.GetCollection<GmailAccountDoc>("gmailAccounts").Find(a => a.Enabled).ToListAsync(ct);
        var states = (await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").Find(_ => true).ToListAsync(ct)).ToDictionary(s => s.AccountId);
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            var connection = await GmailIntegrationModule.ResolveConnectionAsync(svc.Db, account.UserId, account.ConnectionId, ct);
            if (connection is null) continue;
            var cfg = await GmailIntegrationModule.LoadConfigAsync(svc.Db, account.UserId, account.ConnectionId, ct);
            if (!cfg.Sync.Enabled) continue;
            states.TryGetValue(account.ConnectionId, out var state);
            var now = DateTime.UtcNow;

            if (engine.WatchConfigured && cfg.Sync.NearRealTime && (state?.WatchRenewedUtc is null || state.WatchRenewedUtc < now.AddHours(-24)))
            {
                try { await engine.RenewWatchAsync(account.UserId, account.ConnectionId, ct); }
                catch (GmailIntegrationException ex) { logger.LogWarning("Gmail watch renewal failed for {AccountId}: {Message}", account.ConnectionId, ex.Message); }
            }

            var watchActive = state?.WatchExpiresUtc > now && cfg.Sync.NearRealTime;
            var interval = watchActive ? Reconcile : TimeSpan.FromMinutes(cfg.Sync.IntervalMinutes);
            // Back off on repeated failures: 2, 4, 8 … up to 60 minutes.
            if (state?.ConsecutiveFailures > 0) interval = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, state.ConsecutiveFailures)));
            var due = state is null || state.SyncRequested || state.LastAttemptUtc is null || now - state.LastAttemptUtc >= interval;
            if (!due) continue;
            await engine.SyncAsync(account.UserId, account.ConnectionId, state?.SyncRequested == true ? "push" : "worker", ct);
        }
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try { await PurgeExpiredAsync(svc.Db, account, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Gmail retention purge failed for {AccountId}", account.ConnectionId); }
        }
        await svc.Db.GetCollection<GmailWebhookEvent>("gmailWebhookEvents").DeleteManyAsync(e => e.ExpiresAtUtc < DateTime.UtcNow, ct); // belt and braces beside the TTL index
    }
}
