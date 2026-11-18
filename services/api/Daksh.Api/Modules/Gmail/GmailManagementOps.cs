using System.Text.Json;
using Daksh.Api.Auth;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public static partial class GmailIntegrationModule
{
    private static void MapRules(RouteGroupBuilder g)
    {
        g.MapGet("/rules", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var rules = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(r => r.UserId == who.UserId && r.AccountId == connection.Id).SortBy(r => r.Priority).ToListAsync(ct);
            return Results.Ok(new { rules, fields = GmailRuleEngine.Fields, actionTypes = GmailRuleEngine.ActionTypes, destructive = GmailRuleEngine.Destructive });
        }));
        g.MapPost("/rules", (string? accountId, GmailRule rule, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var errors = GmailRuleEngine.Validate(rule);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (rule.RunAutomatically && rule.Actions.Any(a => GmailRuleEngine.Destructive.Contains(a.Type)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["runAutomatically"] = ["Rules that trash or forward mail can't run automatically. Run them manually after previewing."] });
            var count = await svc.Db.GetCollection<GmailRule>("gmailRules").CountDocumentsAsync(r => r.UserId == who.UserId && r.AccountId == connection.Id, cancellationToken: ct);
            if (count >= 100) return Results.Problem(statusCode: 409, title: "You've reached the limit of 100 rules for this account.");
            rule.Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(); rule.UserId = who.UserId; rule.AccountId = connection.Id; rule.Version = 1;
            rule.CreatedAtUtc = rule.UpdatedAtUtc = DateTime.UtcNow;
            await svc.Db.GetCollection<GmailRule>("gmailRules").InsertOneAsync(rule, cancellationToken: ct);
            await svc.Audit(who.UserId, connection.Id, "rule.create", ruleId: rule.Id, ct: ct);
            return Results.Ok(rule);
        }));
        g.MapPut("/rules/{id}", (string id, GmailRule rule, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var rules = svc.Db.GetCollection<GmailRule>("gmailRules");
            var existing = await rules.Find(r => r.Id == id && r.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (existing is null) return Results.NotFound();
            var errors = GmailRuleEngine.Validate(rule);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (rule.RunAutomatically && rule.Actions.Any(a => GmailRuleEngine.Destructive.Contains(a.Type)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["runAutomatically"] = ["Rules that trash or forward mail can't run automatically."] });
            rule.Id = id; rule.UserId = who.UserId; rule.AccountId = existing.AccountId; rule.CreatedAtUtc = existing.CreatedAtUtc; rule.UpdatedAtUtc = DateTime.UtcNow; rule.Version = existing.Version + 1;
            var result = await rules.ReplaceOneAsync(r => r.Id == id && r.UserId == who.UserId && r.Version == existing.Version, rule, cancellationToken: ct);
            if (result.MatchedCount == 0) return Results.Problem(statusCode: 409, title: "This rule changed elsewhere. Reload and try again.", extensions: new Dictionary<string, object?> { ["code"] = "version_conflict" });
            await svc.Audit(who.UserId, existing.AccountId, "rule.update", ruleId: id, ct: ct);
            return Results.Ok(rule);
        }));
        g.MapDelete("/rules/{id}", (string id, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var rule = await svc.Db.GetCollection<GmailRule>("gmailRules").FindOneAndDeleteAsync(r => r.Id == id && r.UserId == who.UserId, cancellationToken: ct);
            if (rule is null) return Results.NotFound();
            await svc.Audit(who.UserId, rule.AccountId, "rule.delete", ruleId: id, ct: ct);
            return Results.NoContent();
        }));
        g.MapPost("/rules/{id}/duplicate", (string id, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var rules = svc.Db.GetCollection<GmailRule>("gmailRules");
            var source = await rules.Find(r => r.Id == id && r.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (source is null) return Results.NotFound();
            source.Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(); source.Name = (source.Name.Length > 90 ? source.Name[..90] : source.Name) + " (copy)";
            source.Enabled = false; source.RunAutomatically = false; source.Version = 1; source.CreatedAtUtc = source.UpdatedAtUtc = DateTime.UtcNow;
            await rules.InsertOneAsync(source, cancellationToken: ct);
            await svc.Audit(who.UserId, source.AccountId, "rule.duplicate", ruleId: source.Id, ct: ct);
            return Results.Ok(source);
        }));
        g.MapPost("/rules/{id}/enabled", (string id, bool on, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var r = await svc.Db.GetCollection<GmailRule>("gmailRules").UpdateOneAsync(x => x.Id == id && x.UserId == who.UserId, Builders<GmailRule>.Update.Set(x => x.Enabled, on).Inc(x => x.Version, 1).Set(x => x.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
            return r.MatchedCount == 0 ? Results.NotFound() : Results.Ok(new { id, enabled = on });
        }));
        g.MapPost("/rules/test", (RuleTestRequest r, GmailServices svc) => Run(svc, () =>
        {
            if (r.Rule is null || r.Sample is null) return Task.FromResult(Results.BadRequest("Provide a rule and a sample message."));
            var errors = GmailRuleEngine.Validate(r.Rule);
            if (errors.Count > 0) return Task.FromResult(Results.ValidationProblem(errors));
            var result = GmailRuleEngine.Evaluate(r.Rule, r.Sample);
            return Task.FromResult(Results.Ok(new { matched = result.Matched, reasons = result.Reasons, wouldRun = result.Matched ? r.Rule.Actions.Select(a => a.Type).ToArray() : [], needsConfirmation = r.Rule.Actions.Any(a => GmailRuleEngine.Destructive.Contains(a.Type)) }));
        }));
        g.MapPost("/rules/{id}/preview", (string id, RulePreviewRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var rule = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(x => x.Id == id && x.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (rule is null) return Results.NotFound();
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, rule.AccountId, GmailPermission.Read, ct);
            var max = Math.Clamp(r.Max, 1, 50);
            using var list = await svc.Api(who.UserId, auth.Connection.Id, QueryHelpers.AddQueryString("users/me/messages", new Dictionary<string, string?> { ["q"] = string.IsNullOrWhiteSpace(r.Query) ? "in:inbox" : r.Query, ["maxResults"] = max.ToString() }), ct: ct);
            var ids = list.RootElement.TryGetProperty("messages", out var arr) ? arr.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList() : [];
            var matches = new List<object>();
            foreach (var mid in ids)
            {
                var m = await svc.Fetch(who.UserId, auth.Connection.Id, mid, ct);
                var cls = GmailIntelligence.Classify(GmailMessageParser.ToInput(m), null);
                if (GmailRuleEngine.Evaluate(rule, GmailMessageParser.ToFacts(m, cls)).Matched)
                    matches.Add(new { m.Id, from = m.Header("From"), subject = m.Header("Subject"), date = m.Date });
            }
            return Results.Ok(new { scanned = ids.Count, matched = matches.Count, matches, actions = rule.Actions.Select(a => a.Type), destructive = rule.Actions.Any(a => GmailRuleEngine.Destructive.Contains(a.Type)) });
        }));
        g.MapPost("/rules/{id}/run", (string id, RuleRunRequest r, GmailServices svc, GmailPipeline pipe, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var rule = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(x => x.Id == id && x.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (rule is null) return Results.NotFound();
            var ids = (r.MessageIds ?? []).Distinct().Take(50).ToList();
            if (ids.Count == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["messageIds"] = ["Choose the messages to run this rule on (preview first)."] });
            var destructive = rule.Actions.Any(a => GmailRuleEngine.Destructive.Contains(a.Type));
            if (destructive && !r.Confirm)
                return Results.Problem(statusCode: 409, title: $"This rule trashes or forwards mail and would change {ids.Count} message(s). Confirm to continue.", extensions: new Dictionary<string, object?> { ["code"] = "confirmation_required", ["count"] = ids.Count });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, rule.AccountId, GmailPermission.Read, ct);
            var runs = new List<GmailRuleRun>();
            foreach (var mid in ids)
            {
                var m = await svc.Fetch(who.UserId, auth.Connection.Id, mid, ct);
                var cls = GmailIntelligence.Classify(GmailMessageParser.ToInput(m), null);
                if (!GmailRuleEngine.Evaluate(rule, GmailMessageParser.ToFacts(m, cls)).Matched) continue;
                runs.Add(await pipe.ExecuteRuleAsync(auth.Account, auth.Config, rule, m, cls, "manual", r.Confirm, ct));
            }
            return Results.Ok(new { ran = runs.Count, runs });
        }));
        g.MapGet("/rules/{id}/runs", (string id, int? take, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
            Results.Ok(new { runs = await svc.Db.GetCollection<GmailRuleRun>("gmailRuleRuns").Find(r => r.RuleId == id && r.UserId == who.UserId).SortByDescending(r => r.AtUtc).Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct) })));
    }

    private static void MapOperations(RouteGroupBuilder g)
    {
        // ── follow-ups ─────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/follow-ups", (string? accountId, string? status, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var f = Builders<GmailFollowUp>.Filter.Eq(x => x.UserId, who.UserId) & Builders<GmailFollowUp>.Filter.Eq(x => x.AccountId, connection.Id);
            if (!string.IsNullOrWhiteSpace(status) && GmailFollowUpPlanner.Statuses.Contains(status)) f &= Builders<GmailFollowUp>.Filter.Eq(x => x.Status, status);
            var items = await svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps").Find(f).SortBy(x => x.DueAtUtc).Limit(200).ToListAsync(ct);
            return Results.Ok(new { items, statuses = GmailFollowUpPlanner.Statuses });
        }));
        g.MapPatch("/follow-ups/{threadId}", (string threadId, string? accountId, FollowUpUpdate r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.NotFound();
            var update = Builders<GmailFollowUp>.Update.Set(x => x.UpdatedAtUtc, DateTime.UtcNow);
            if (r.Status is not null)
            {
                if (r.Status is not (GmailFollowUpPlanner.Completed or GmailFollowUpPlanner.Ignored or GmailFollowUpPlanner.AwaitingMyResponse or GmailFollowUpPlanner.AwaitingTheirResponse))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["You can mark a follow-up completed, ignored or awaiting a response."] });
                update = update.Set(x => x.Status, r.Status);
            }
            if (r.SnoozeUntil is { } snooze) update = update.Set(x => x.SnoozedUntilUtc, snooze.ToUniversalTime());
            if (r.DueAt is { } due) update = update.Set(x => x.DueAtUtc, due.ToUniversalTime());
            var result = await svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps").FindOneAndUpdateAsync(x => x.Key == $"{connection.Id}:{threadId}" && x.UserId == who.UserId, update,
                new FindOneAndUpdateOptions<GmailFollowUp> { ReturnDocument = ReturnDocument.After }, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }));
        g.MapPost("/follow-ups/{threadId}/draft", (string threadId, string? accountId, GmailServices svc, GmailPipeline pipe, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateDrafts, ct);
            var followUps = svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps");
            var f = await followUps.Find(x => x.Key == $"{auth.Connection.Id}:{threadId}" && x.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (f is null) return Results.NotFound();
            var messages = await FetchThreadAsync(svc, who.UserId, auth.Connection.Id, threadId, ct);
            var last = messages.LastOrDefault(m => m.Labels.Contains("SENT")) ?? messages.LastOrDefault();
            if (last is null) return Results.NotFound();
            var name = GmailPipeline.DisplayName(last.Header("To"));
            var rc = auth.Config.Reply;
            var body = $"{(string.IsNullOrWhiteSpace(name) ? rc.Greeting : $"{rc.Greeting} {name}")},\n\nI'm following up on my earlier message regarding \"{f.Subject}\". Could you let me know where things stand?\n\n{rc.Closing}{(string.IsNullOrWhiteSpace(rc.Signature) ? "" : ",\n" + rc.Signature.Trim())}";
            var (inReplyTo, refs, subject) = GmailServices.ReplyHeaders(last);
            var raw = GmailServices.BuildMime(auth.Account.Email, last.Header("To"), null, subject, body, inReplyTo, refs);
            using var created = await svc.Api(who.UserId, auth.Connection.Id, "users/me/drafts", HttpMethod.Post, new { message = new { raw, threadId } }, ct);
            var draftId = created.RootElement.GetProperty("id").GetString();
            await followUps.UpdateOneAsync(x => x.Key == f.Key, Builders<GmailFollowUp>.Update.Set(x => x.Status, GmailFollowUpPlanner.FollowUpDrafted).Set(x => x.DraftId, draftId).Set(x => x.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "followUp.draft", last.Id, ct: ct);
            return Results.Ok(new { draftId, body });
        }));

        // ── notifications ──────────────────────────────────────────────────────────────────────────────
        g.MapGet("/notifications", (string? accountId, bool? unread, bool? includeSuppressed, int? take, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var f = Builders<GmailNotification>.Filter.Eq(n => n.UserId, who.UserId);
            if (!string.IsNullOrWhiteSpace(accountId)) f &= Builders<GmailNotification>.Filter.Eq(n => n.AccountId, accountId);
            if (unread == true) f &= Builders<GmailNotification>.Filter.Eq(n => n.Read, false);
            if (includeSuppressed != true) f &= Builders<GmailNotification>.Filter.Eq(n => n.Suppressed, false);
            var col = svc.Db.GetCollection<GmailNotification>("gmailNotifications");
            var items = await col.Find(f).SortByDescending(n => n.CreatedAtUtc).Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
            var unreadCount = await col.CountDocumentsAsync(n => n.UserId == who.UserId && !n.Read && !n.Suppressed, cancellationToken: ct);
            return Results.Ok(new { items, unreadCount });
        }));
        g.MapPost("/notifications/read", (NotificationsReadRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var col = svc.Db.GetCollection<GmailNotification>("gmailNotifications");
            var filter = Builders<GmailNotification>.Filter.Eq(n => n.UserId, who.UserId);
            if (!r.All) filter &= Builders<GmailNotification>.Filter.In(n => n.Key, (r.Keys ?? []).Take(200));
            var res = await col.UpdateManyAsync(filter, Builders<GmailNotification>.Update.Set(n => n.Read, true), cancellationToken: ct);
            return Results.Ok(new { updated = res.ModifiedCount });
        }));

        // ── audit ──────────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/audit", (string? accountId, string? action, string? status, string? trigger, string? q, int? skip, int? take, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var b = Builders<GmailActionAudit>.Filter;
            var f = b.Eq(a => a.UserId, who.UserId);
            if (!string.IsNullOrWhiteSpace(accountId)) f &= b.Eq(a => a.AccountId, accountId);
            if (!string.IsNullOrWhiteSpace(status)) f &= b.Eq(a => a.Status, status);
            if (!string.IsNullOrWhiteSpace(trigger)) f &= b.Eq(a => a.Trigger, trigger);
            if (!string.IsNullOrWhiteSpace(action)) f &= b.Regex(a => a.Action, new MongoDB.Bson.BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(action.Trim()), "i"));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var rx = new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(q.Trim()), "i");
                f &= b.Or(b.Regex(a => a.Action, rx), b.Regex(a => a.MessageRef, rx), b.Regex(a => a.Error, rx));
            }
            var col = svc.Db.GetCollection<GmailActionAudit>("gmailActionAudit");
            var total = await col.CountDocumentsAsync(f, cancellationToken: ct);
            var items = await col.Find(f).SortByDescending(a => a.AtUtc).Skip(Math.Max(0, skip ?? 0)).Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(new { total, items });
        }));

        // ── synchronisation ────────────────────────────────────────────────────────────────────────────
        g.MapGet("/sync/status", (string? accountId, GmailServices svc, GmailSyncEngine engine, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var state = await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").Find(s => s.AccountId == connection.Id).FirstOrDefaultAsync(ct);
            var cfg = await LoadConfigAsync(svc.Db, who.UserId, connection.Id, ct);
            var dead = await svc.Db.GetCollection<GmailDeadLetter>("gmailDeadLetters").CountDocumentsAsync(d => d.AccountId == connection.Id && d.Dead, cancellationToken: ct);
            return Results.Ok(new { state, enabled = cfg.Sync.Enabled, pushConfigured = engine.WatchConfigured, deadLetters = dead });
        }));
        g.MapPost("/sync/run", (string? accountId, GmailServices svc, GmailSyncEngine engine, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            var run = await engine.SyncAsync(who.UserId, auth.Connection.Id, "manual", ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "sync.manual", ct: ct);
            return Results.Ok(run);
        }));
        foreach (var (route, on) in new[] { ("start", true), ("stop", false) })
            g.MapPost($"/sync/{route}", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
            {
                var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
                if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
                var cfg = await LoadConfigAsync(svc.Db, who.UserId, connection.Id, ct);
                cfg.Sync.Enabled = on; cfg.Version++; cfg.UpdatedAtUtc = DateTime.UtcNow;
                await svc.Db.GetCollection<GmailAccountConfig>("gmailAccountConfigs").ReplaceOneAsync(c => c.ConnectionId == connection.Id && c.UserId == who.UserId, cfg, new ReplaceOptions { IsUpsert = true }, ct);
                if (on) await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").UpdateOneAsync(s => s.AccountId == connection.Id, Builders<GmailSyncState>.Update.SetOnInsert(s => s.UserId, who.UserId).Set(s => s.SyncRequested, true), new UpdateOptions { IsUpsert = true }, ct);
                await svc.Audit(who.UserId, connection.Id, on ? "sync.start" : "sync.stop", ct: ct);
                return Results.Ok(new { enabled = on, version = cfg.Version });
            }));
        g.MapGet("/sync/history", (string? accountId, int? take, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            return Results.Ok(new { runs = await svc.Db.GetCollection<GmailSyncRun>("gmailSyncHistory").Find(r => r.AccountId == connection.Id && r.UserId == who.UserId).SortByDescending(r => r.StartedUtc).Limit(Math.Clamp(take ?? 30, 1, 100)).ToListAsync(ct) });
        }));
        g.MapPost("/sync/watch/renew", (string? accountId, GmailServices svc, GmailSyncEngine engine, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (!engine.WatchConfigured) return Results.Problem(statusCode: 503, title: "Push notifications aren't configured on the server (Gmail:PubSub:TopicName).", extensions: new Dictionary<string, object?> { ["code"] = "pubsub_not_configured" });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            return Results.Ok(new { expiresUtc = await engine.RenewWatchAsync(who.UserId, auth.Connection.Id, ct) });
        }));
        g.MapGet("/action-items", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
            Results.Ok(new { items = await svc.Db.GetCollection<GmailActionItem>("gmailActionItems").Find(i => i.UserId == who.UserId && !i.Done).SortByDescending(i => i.CreatedAtUtc).Limit(100).ToListAsync(ct) })));

        // ── export & deletion ──────────────────────────────────────────────────────────────────────────
        g.MapGet("/export", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var account = await LoadAccountAsync(svc.Db, connection, ct);
            var export = new
            {
                exportedAtUtc = DateTime.UtcNow, account = new { account.Email, account.Nickname, account.IsDefault, account.Enabled, scopes = GmailScopes.Parse(account.GrantedScopes) },
                config = await LoadConfigAsync(svc.Db, who.UserId, connection.Id, ct),
                rules = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(r => r.UserId == who.UserId && r.AccountId == connection.Id).ToListAsync(ct),
                savedSearches = await svc.Db.GetCollection<GmailSavedSearch>("gmailSavedSearches").Find(s => s.UserId == who.UserId).ToListAsync(ct),
                followUps = await svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps").Find(f => f.UserId == who.UserId && f.AccountId == connection.Id).ToListAsync(ct),
                corrections = await svc.Db.GetCollection<GmailCorrection>("gmailCorrections").Find(c => c.UserId == who.UserId && c.AccountId == connection.Id).ToListAsync(ct),
                audit = await svc.Db.GetCollection<GmailActionAudit>("gmailActionAudit").Find(a => a.UserId == who.UserId && a.AccountId == connection.Id).SortByDescending(a => a.AtUtc).Limit(1000).ToListAsync(ct),
                note = "OAuth tokens and message bodies are never stored or exported.",
            };
            return Results.Json(export, Web);
        }));
        g.MapDelete("/data", (string? accountId, bool confirm, bool includeAudit, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (!confirm) return Results.Problem(statusCode: 409, title: "This deletes Daksha's saved Gmail data (synced message info, rules, settings). Your Gmail mailbox is not touched. Confirm to continue.", extensions: new Dictionary<string, object?> { ["code"] = "confirmation_required" });
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.NotFound();
            await GmailLocalData.PurgeAccountAsync(svc.Db, who.UserId, connection.Id, keepAudit: !includeAudit, ct);
            await svc.Audit(who.UserId, connection.Id, "data.delete", approval: "Approved", ct: ct);
            return Results.Ok(new { deleted = true, auditKept = !includeAudit });
        }));
    }
}
