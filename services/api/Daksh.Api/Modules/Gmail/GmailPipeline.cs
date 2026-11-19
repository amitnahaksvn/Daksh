using System.Text.Json;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>Daksha-side to-do/reminder created by rules and AI action-item extraction. Kept here until the Tasks module is rebuilt on Mongo.</summary>
[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements]
public sealed class GmailActionItem
{
    [MongoDB.Bson.Serialization.Attributes.BsonId] public required string Key { get; set; }
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public string Kind { get; set; } = "task"; // task | reminder
    public string Title { get; set; } = "";
    public DateTime? DueUtc { get; set; }
    public string? MessageId { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The processing pipeline for one message: exclusions → duplicate check → AI → persist → notifications → automation events →
/// follow-up tracking → automatic rules. One failing step never aborts the others for the same message.
/// </summary>
public sealed class GmailPipeline(GmailServices svc, IServiceProvider services)
{
    public async Task<bool> ProcessAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailFetched m, string trigger, CancellationToken ct)
    {
        var userId = cfg.UserId;
        var accountId = account.ConnectionId;
        if (IsExcluded(cfg.Sync, m)) return false;

        var from = m.Header("From");
        var sender = GmailIntelligence.ExtractAddress(from);
        var outgoing = m.Labels.Contains("SENT") || sender == account.Email.ToLowerInvariant();
        var corrections = await svc.Db.GetCollection<GmailCorrection>("gmailCorrections").Find(c => c.UserId == userId && c.AccountId == accountId && c.Sender == sender).ToListAsync(ct);

        GmailClassificationResult? cls = null;
        IReadOnlyList<string> categories = [];
        if (GmailIntelligence.ShouldProcess(cfg.Ai, from, m.Labels))
        {
            var result = GmailIntelligence.Classify(GmailMessageParser.ToInput(m), corrections, cfg.Ai.SummaryLength);
            if (GmailIntelligence.ShouldProcess(cfg.Ai, from, m.Labels, result))
            {
                cls = result;
                categories = GmailIntelligence.EffectiveCategories(result, cfg.Ai);
            }
        }

        var docs = svc.Db.GetCollection<GmailMessageDoc>("gmailMessages");
        var key = $"{accountId}:{m.Id}";
        var existing = await docs.Find(d => d.Key == key).FirstOrDefaultAsync(ct);
        var duplicate = cls is not null && cfg.Ai.Features.TryGetValue("duplicate", out var dup) && dup.Enabled &&
                        await docs.Find(d => d.UserId == userId && d.AccountId == accountId && d.ContentHash == cls.ContentHash && d.Key != key).AnyAsync(ct);
        var keepText = cfg.Privacy.StoreSnippets;
        await docs.ReplaceOneAsync(d => d.Key == key, new GmailMessageDoc
        {
            Key = key, UserId = userId, AccountId = accountId, MessageId = m.Id, ThreadId = m.ThreadId, Labels = m.Labels.ToList(),
            From = from, To = m.Header("To"), Subject = keepText ? m.Header("Subject") : "", Snippet = keepText ? m.Snippet : "",
            DateUtc = m.Date.UtcDateTime, HistoryId = m.HistoryId, HasAttachment = m.Attachments.Count > 0, SyncedAtUtc = DateTime.UtcNow,
            Processed = cls is not null, ProcessedAtUtc = cls is null ? null : DateTime.UtcNow,
            Categories = categories.ToList(), Scores = cls?.Scores.ToDictionary(k => k.Key, k => k.Value) ?? [],
            Important = cls?.Important ?? false, Urgent = cls?.Urgent ?? false, Suspicious = cls?.Suspicious ?? false, NeedsReply = cls?.NeedsReply ?? false,
            Sentiment = cls?.Sentiment ?? "neutral", Summary = keepText && cls is not null ? cls.Summary : "",
            ActionItems = keepText && cls is not null ? cls.ActionItems.ToList() : [], Deadlines = cls?.Deadlines.Select(x => x.Date.UtcDateTime).ToList() ?? [],
            ContentHash = cls?.ContentHash ?? "", IsDuplicate = duplicate, CorrectedBy = cls?.CorrectedBy,
            ReplyEvaluated = existing?.ReplyEvaluated ?? outgoing,
        }, new ReplaceOptions { IsUpsert = true }, ct);

        if (!outgoing)
            await Step("otp-forward", async () =>
            {
                if (!OtpExtractor.LooksLikeOtp(m.Header("Subject"), m.Text)) return;
                if (OtpExtractor.Extract(m.Header("Subject"), m.Text) is { } code)
                    await OtpForwarder.ForwardAsync(services, svc.Db, userId, $"{accountId}:{m.Id}", from, code, m.Date, svc.Log, ct);
            });
        await Step("notifications", () => NotifyAsync(cfg, m, cls, categories, outgoing, ct));
        await Step("events", () => EmitAsync(cfg, m, cls, categories, outgoing, ct));
        await Step("follow-up", () => TrackFollowUpAsync(account, cfg, m, cls, outgoing, ct));
        if (!outgoing && cls?.Suspicious != true)
            await Step("rules", () => RunAutomaticRulesAsync(account, cfg, m, cls, ct));
        return true;

        async Task Step(string name, Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                svc.Log.LogWarning(ex, "Gmail pipeline step {Step} failed for message {MessageId}", name, m.Id);
                await svc.Audit(userId, accountId, $"pipeline.{name}", m.Id, "Sync", "Failed", ex.Message, ct: CancellationToken.None);
            }
        }
    }

    public static bool IsExcluded(SyncConfig sync, GmailFetched m)
    {
        var sender = GmailIntelligence.ExtractAddress(m.Header("From"));
        var domain = sender.Contains('@') ? sender[(sender.IndexOf('@') + 1)..] : "";
        if (sync.ExcludedSenders.Contains(sender, StringComparer.OrdinalIgnoreCase)) return true;
        if (sync.ExcludedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return true;
        var subject = m.Header("Subject").ToLowerInvariant();
        if (sync.ExcludedSubjectKeywords.Any(k => subject.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
        if (sync.ExcludedLabels.Any(l => m.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))) return true;
        if (m.Labels.Contains("SPAM") && !sync.IncludeSpam) return true;
        if (m.Labels.Contains("TRASH") && !sync.IncludeTrash) return true;
        // Only the labels the user chose to synchronise (empty list = everything).
        if (sync.Labels.Count > 0 && !sync.Labels.Any(l => m.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)) &&
            !(sync.IncludeSpam && m.Labels.Contains("SPAM")) && !(sync.IncludeTrash && m.Labels.Contains("TRASH"))) return true;
        return false;
    }

    private Task NotifyAsync(GmailAccountConfig cfg, GmailFetched m, GmailClassificationResult? cls, IReadOnlyList<string> categories, bool outgoing, CancellationToken ct)
    {
        if (outgoing) return Task.CompletedTask;
        var subject = m.Header("Subject");
        var from = m.Header("From");
        var tasks = new List<Task>();
        void N(string type, string title) => tasks.Add(svc.Notify(cfg, type, m.Id, title, $"{from}: {subject}", m.Id, m.ThreadId, ct));
        if (m.Labels.Contains("INBOX") && m.Labels.Contains("UNREAD")) N("newEmail", "New email");
        if (cls?.Important == true) N("importantEmail", "Important email");
        if (cls?.Urgent == true) N("urgentEmail", "Urgent email");
        if (categories.Contains("invoice")) N("invoiceReceived", "Invoice received");
        if (categories.Contains("paymentReminder")) N("paymentReminder", "Payment reminder");
        if (categories.Contains("meetingRequest")) N("meetingInvitation", "Meeting invitation");
        if (cls is { ActionItems.Count: > 0 } && cfg.Ai.Features.GetValueOrDefault("actionItems")?.Enabled == true) N("actionRequired", "Action required");
        if (m.Attachments.Count > 0) N("attachmentReceived", "Attachment received");
        if (cfg.Notifications.Types.GetValueOrDefault("selectedSender") && cfg.Reply.OnlySenders.Contains(GmailIntelligence.ExtractAddress(from))) N("selectedSender", "Email from a selected sender");
        if (cls is { Deadlines.Count: > 0 } && cfg.FollowUp.NotifyBeforeDeadline)
        {
            var next = cls.Deadlines.Where(d => d.Date >= DateTimeOffset.UtcNow.Date).OrderBy(d => d.Date).FirstOrDefault();
            if (next is not null && next.Date <= DateTimeOffset.UtcNow.AddHours(cfg.FollowUp.DeadlineLeadHours))
                tasks.Add(svc.Notify(cfg, "deadlineApproaching", $"{m.Id}:{next.Date:yyyyMMdd}", "Deadline approaching", $"{subject} — due {next.Date:yyyy-MM-dd}", m.Id, m.ThreadId, ct));
        }
        if (cls is not null) tasks.Add(svc.Notify(cfg, "aiProcessingCompleted", m.Id, "AI processing completed", subject, m.Id, m.ThreadId, ct));
        return Task.WhenAll(tasks);
    }

    private Task EmitAsync(GmailAccountConfig cfg, GmailFetched m, GmailClassificationResult? cls, IReadOnlyList<string> categories, bool outgoing, CancellationToken ct)
    {
        var data = new Dictionary<string, string> { ["subject"] = m.Header("Subject"), ["from"] = m.Header("From") };
        var tasks = new List<Task>();
        void E(string trigger) => tasks.Add(svc.Emit(cfg, trigger, m.Id, m.Id, m.ThreadId, data, ct));
        if (outgoing) { E("email_sent"); return Task.WhenAll(tasks); }
        E("new_email");
        if (cls?.Important == true) E("important_email");
        if (cls?.Urgent == true) E("urgent_email");
        if (cfg.Reply.OnlySenders.Contains(GmailIntelligence.ExtractAddress(m.Header("From")))) E("email_from_selected_sender");
        if (m.Attachments.Count > 0) E("email_with_attachment");
        if (categories.Contains("invoice")) E("invoice_detected");
        if (categories.Contains("receipt")) E("receipt_detected");
        if (categories.Contains("meetingRequest")) E("meeting_invitation_detected");
        if (cls is { Deadlines.Count: > 0 }) E("deadline_detected");
        if (cls is { ActionItems.Count: > 0 }) E("action_item_detected");
        if (cls is not null) E("email_classified");
        return Task.WhenAll(tasks);
    }

    private async Task TrackFollowUpAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailFetched m, GmailClassificationResult? cls, bool outgoing, CancellationToken ct)
    {
        if (!cfg.FollowUp.Enabled || m.ThreadId.Length == 0 || m.Labels.Contains("DRAFT")) return;
        var collection = svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps");
        var key = $"{account.ConnectionId}:{m.ThreadId}";
        var existing = await collection.Find(f => f.Key == key).FirstOrDefaultAsync(ct);
        var when = m.Date.UtcDateTime;
        if (existing is not null && when < existing.LastActivityUtc) return; // out-of-order delivery: an older message can't rewind the status
        if (existing?.Status is GmailFollowUpPlanner.Ignored) return;

        var now = DateTime.UtcNow;
        string status; DateTime? due; string counterparty;
        if (outgoing)
        {
            status = GmailFollowUpPlanner.AwaitingTheirResponse;
            due = GmailFollowUpPlanner.DueAt(when, cfg.FollowUp.IntervalsDays, existing?.FollowUpsSent ?? 0).UtcDateTime;
            counterparty = m.Header("To");
        }
        else
        {
            if (existing is { Status: GmailFollowUpPlanner.AwaitingTheirResponse or GmailFollowUpPlanner.FollowUpDue or GmailFollowUpPlanner.FollowUpDrafted })
            {
                // They replied: the follow-up is no longer needed.
                await collection.UpdateOneAsync(f => f.Key == key, Builders<GmailFollowUp>.Update.Set(f => f.Status, GmailFollowUpPlanner.Completed)
                    .Set(f => f.LastActivityUtc, when).Set(f => f.UpdatedAtUtc, now), cancellationToken: ct);
                await svc.Emit(cfg, "email_reply_received", m.Id, m.Id, m.ThreadId, null, ct);
                if (cls?.NeedsReply != true) return;
            }
            if (cls?.NeedsReply != true) return;
            status = GmailFollowUpPlanner.AwaitingMyResponse;
            due = when.AddDays(1);
            counterparty = m.Header("From");
        }
        await collection.UpdateOneAsync(f => f.Key == key,
            Builders<GmailFollowUp>.Update
                .SetOnInsert(f => f.UserId, cfg.UserId).SetOnInsert(f => f.AccountId, account.ConnectionId).SetOnInsert(f => f.ThreadId, m.ThreadId)
                .SetOnInsert(f => f.CreatedAtUtc, now)
                .Set(f => f.MessageId, m.Id).Set(f => f.Subject, m.Header("Subject")).Set(f => f.Counterparty, counterparty)
                .Set(f => f.Status, status).Set(f => f.DueAtUtc, due).Set(f => f.SnoozedUntilUtc, null).Set(f => f.LastActivityUtc, when).Set(f => f.UpdatedAtUtc, now),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    private async Task RunAutomaticRulesAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailFetched m, GmailClassificationResult? cls, CancellationToken ct)
    {
        if (!GmailPermissionPolicy.IsAllowed(cfg.Permissions.Profile, cfg.Permissions.Overrides, GmailPermission.RunRules)) return;
        var rules = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(r => r.UserId == cfg.UserId && r.AccountId == account.ConnectionId && r.Enabled && r.RunAutomatically).ToListAsync(ct);
        if (rules.Count == 0) return;
        var facts = GmailMessageParser.ToFacts(m, cls);
        foreach (var (rule, _) in GmailRuleEngine.MatchingRules(rules, facts))
        {
            var runs = svc.Db.GetCollection<GmailRuleRun>("gmailRuleRuns");
            if (await runs.Find(r => r.RuleId == rule.Id && r.MessageId == m.Id && r.Trigger == "automatic").AnyAsync(ct)) continue; // duplicate-execution guard
            await ExecuteRuleAsync(account, cfg, rule, m, cls, "automatic", confirmDestructive: false, ct);
        }
    }

    /// <summary>Runs a rule's actions on one message. Each action is permission- and scope-checked; failures are recorded, not thrown.</summary>
    public async Task<GmailRuleRun> ExecuteRuleAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailRule rule, GmailFetched m,
        GmailClassificationResult? cls, string trigger, bool confirmDestructive, CancellationToken ct)
    {
        var userId = cfg.UserId; var accountId = account.ConnectionId;
        var run = new GmailRuleRun { UserId = userId, AccountId = accountId, RuleId = rule.Id, RuleName = rule.Name, MessageId = m.Id, Trigger = trigger, AtUtc = DateTime.UtcNow };
        var granted = GmailScopes.Parse(account.GrantedScopes);
        foreach (var action in rule.Actions)
        {
            var label = action.Value.Length > 0 ? $"{action.Type}({action.Value})" : action.Type;
            var permission = GmailRuleEngine.PermissionFor(action.Type);
            var daksha = GmailRuleEngine.DakshaSideActions.Contains(action.Type);
            if (!daksha)
            {
                if (!GmailPermissionPolicy.IsAllowed(cfg.Permissions.Profile, cfg.Permissions.Overrides, permission)) { run.Skipped.Add($"{label}: not permitted ({cfg.Permissions.Profile} profile)"); continue; }
                var needed = GmailPermission.RequiredScope(permission);
                if (needed is not null && !GmailScopes.Satisfies(granted, needed)) { run.Skipped.Add($"{label}: needs additional Google permission"); continue; }
            }
            if (GmailRuleEngine.Destructive.Contains(action.Type) && !(confirmDestructive && trigger == "manual"))
            { run.Skipped.Add($"{label}: needs explicit confirmation (never runs automatically)"); continue; }
            try
            {
                await ApplyActionAsync(account, cfg, action, m, cls, rule, ct);
                run.Applied.Add(label);
                await svc.Audit(userId, accountId, $"rule.{action.Type}", m.Id, trigger == "automatic" ? "Rule" : "User", "Success", ruleId: rule.Id,
                    approval: trigger == "automatic" ? "Approved" : confirmDestructive ? "Approved" : "NotRequired", ct: CancellationToken.None);
            }
            catch (GmailIntegrationException ex)
            {
                run.Skipped.Add($"{label}: {ex.Message}");
                await svc.Audit(userId, accountId, $"rule.{action.Type}", m.Id, trigger == "automatic" ? "Rule" : "User", "Failed", ex.Message, rule.Id, ct: CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                run.Skipped.Add($"{label}: Gmail could not be reached");
            }
        }
        run.Status = run.Skipped.Count == 0 ? "ok" : run.Applied.Count == 0 ? "failed" : "partial";
        if (trigger != "test") await svc.Db.GetCollection<GmailRuleRun>("gmailRuleRuns").InsertOneAsync(run, cancellationToken: ct);
        return run;
    }

    private async Task ApplyActionAsync(GmailAccountDoc account, GmailAccountConfig cfg, GmailRuleAction action, GmailFetched m, GmailClassificationResult? cls, GmailRule rule, CancellationToken ct)
    {
        var userId = cfg.UserId; var accountId = account.ConnectionId;
        switch (action.Type)
        {
            case "addLabel":
            {
                var id = await FindLabelAsync(userId, accountId, action.Value, ct) ?? throw new GmailIntegrationException(404, $"The label \"{action.Value}\" doesn't exist.");
                await svc.ModifyLabels(userId, accountId, m.Id, [id], [], ct); break;
            }
            case "createLabel":
            {
                var id = await svc.EnsureLabel(userId, accountId, action.Value, ct);
                await svc.ModifyLabels(userId, accountId, m.Id, [id], [], ct); break;
            }
            case "removeLabel":
            {
                var id = await FindLabelAsync(userId, accountId, action.Value, ct) ?? throw new GmailIntegrationException(404, $"The label \"{action.Value}\" doesn't exist.");
                await svc.ModifyLabels(userId, accountId, m.Id, [], [id], ct); break;
            }
            case "markRead": await svc.ModifyLabels(userId, accountId, m.Id, [], ["UNREAD"], ct); break;
            case "markUnread": await svc.ModifyLabels(userId, accountId, m.Id, ["UNREAD"], [], ct); break;
            case "star": await svc.ModifyLabels(userId, accountId, m.Id, ["STARRED"], [], ct); break;
            case "unstar": await svc.ModifyLabels(userId, accountId, m.Id, [], ["STARRED"], ct); break;
            case "markImportant": await svc.ModifyLabels(userId, accountId, m.Id, ["IMPORTANT"], [], ct); break;
            case "archive": await svc.ModifyLabels(userId, accountId, m.Id, [], ["INBOX"], ct); break;
            case "trash": using (await svc.Api(userId, accountId, $"users/me/messages/{Uri.EscapeDataString(m.Id)}/trash", HttpMethod.Post, null, ct)) { } break;
            case "forward":
            {
                if (!await IsVerifiedForwardingAddressAsync(userId, accountId, action.Value, ct))
                    throw new GmailIntegrationException(409, $"{action.Value} isn't a verified forwarding address on this Gmail account.");
                var raw = GmailServices.BuildMime(account.Email, action.Value, null, "Fwd: " + m.Header("Subject"),
                    $"---------- Forwarded message ----------\nFrom: {m.Header("From")}\nDate: {m.Date:R}\nSubject: {m.Header("Subject")}\n\n{m.Text}");
                using (await svc.Api(userId, accountId, "users/me/messages/send", HttpMethod.Post, new { raw }, ct)) { }
                break;
            }
            case "generateDraft":
            {
                var body = GmailReplyComposer.Compose(cfg.Reply, DisplayName(m.Header("From")), m.Header("Subject"), cls?.Summary);
                await CreateReplyDraftAsync(userId, accountId, account.Email, m, body, ct); break;
            }
            case "createTask":
            case "createReminder":
                await svc.Db.GetCollection<GmailActionItem>("gmailActionItems").ReplaceOneAsync(i => i.Key == $"{accountId}:{rule.Id}:{m.Id}:{action.Type}",
                    new GmailActionItem
                    {
                        Key = $"{accountId}:{rule.Id}:{m.Id}:{action.Type}", UserId = userId, AccountId = accountId, Kind = action.Type == "createTask" ? "task" : "reminder",
                        Title = action.Value.Length > 0 ? action.Value : m.Header("Subject"), MessageId = m.Id, CreatedAtUtc = DateTime.UtcNow,
                        DueUtc = action.Type == "createReminder" ? DateTime.UtcNow.AddDays(1) : cls?.Deadlines.FirstOrDefault()?.Date.UtcDateTime,
                    }, new ReplaceOptions { IsUpsert = true }, ct);
                break;
            case "notify":
                await svc.Notify(cfg, "actionRequired", $"{rule.Id}:{m.Id}", rule.Name, $"{m.Header("From")}: {m.Header("Subject")}", m.Id, m.ThreadId, ct); break;
            case "runWorkflow":
                await svc.Emit(cfg, $"workflow:{action.Value}", $"{rule.Id}:{m.Id}", m.Id, m.ThreadId, new() { ["workflow"] = action.Value, ["rule"] = rule.Name }, ct); break;
        }
    }

    public async Task<string?> FindLabelAsync(string userId, string accountId, string name, CancellationToken ct)
    {
        using var list = await svc.Api(userId, accountId, "users/me/labels", ct: ct);
        if (!list.RootElement.TryGetProperty("labels", out var labels)) return null;
        foreach (var l in labels.EnumerateArray())
            if (string.Equals(l.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase) || string.Equals(l.GetProperty("id").GetString(), name, StringComparison.Ordinal))
                return l.GetProperty("id").GetString();
        return null;
    }

    public async Task<bool> IsVerifiedForwardingAddressAsync(string userId, string accountId, string address, CancellationToken ct)
    {
        using var list = await svc.Api(userId, accountId, "users/me/settings/forwardingAddresses", ct: ct);
        if (!list.RootElement.TryGetProperty("forwardingAddresses", out var arr)) return false;
        return arr.EnumerateArray().Any(a =>
            string.Equals(a.GetProperty("forwardingEmail").GetString(), address, StringComparison.OrdinalIgnoreCase) &&
            a.TryGetProperty("verificationStatus", out var v) && v.GetString() == "accepted");
    }

    public async Task<string> CreateReplyDraftAsync(string userId, string accountId, string selfEmail, GmailFetched original, string body, CancellationToken ct)
    {
        var (inReplyTo, refs, subject) = GmailServices.ReplyHeaders(original);
        var replyTo = original.Header("Reply-To");
        var to = string.IsNullOrWhiteSpace(replyTo) ? original.Header("From") : replyTo;
        var raw = GmailServices.BuildMime(selfEmail, to, null, subject, body, inReplyTo, refs);
        using var created = await svc.Api(userId, accountId, "users/me/drafts", HttpMethod.Post, new { message = new { raw, threadId = original.ThreadId } }, ct);
        return created.RootElement.GetProperty("id").GetString() ?? "";
    }

    public static string DisplayName(string from)
    {
        var lt = from.IndexOf('<');
        var name = (lt > 0 ? from[..lt] : "").Trim().Trim('"');
        return name.Length > 0 ? name.Split(' ')[0] : "";
    }

    /// <summary>
    /// Applies the reply policy to messages that need a reply and have waited out the configured delay. Creates drafts, and sends only when
    /// every gate in <see cref="GmailReplyPolicy"/> passes. At most one automatic reply per thread, ever.
    /// </summary>
    public async Task EvaluateRepliesAsync(GmailAccountDoc account, GmailAccountConfig cfg, CancellationToken ct)
    {
        if (!cfg.Reply.SuggestionsEnabled || cfg.Reply.Mode == "AskFirst" && !cfg.Reply.AutoDraft) return;
        var userId = cfg.UserId; var accountId = account.ConnectionId;
        var cutoff = DateTime.UtcNow.AddMinutes(-cfg.Reply.ReplyDelayMinutes);
        var since = DateTime.UtcNow.AddDays(-3);
        var candidates = await svc.Db.GetCollection<GmailMessageDoc>("gmailMessages")
            .Find(d => d.UserId == userId && d.AccountId == accountId && d.NeedsReply && !d.ReplyEvaluated && d.DateUtc <= cutoff && d.DateUtc >= since)
            .SortBy(d => d.DateUtc).Limit(10).ToListAsync(ct);
        var replyLog = svc.Db.GetCollection<GmailReplyLogEntry>("gmailReplyLog");
        foreach (var doc in candidates)
        {
            try
            {
                var fetched = await svc.Fetch(userId, accountId, doc.MessageId, ct);
                var cls = GmailIntelligence.Classify(GmailMessageParser.ToInput(fetched), null, cfg.Ai.SummaryLength);
                var now = DateTimeOffset.UtcNow;
                var already = await replyLog.Find(r => r.Key == $"{accountId}:{doc.ThreadId}").AnyAsync(ct);
                var sinceHour = now.AddHours(-1).UtcDateTime; var sinceDay = now.AddDays(-1).UtcDateTime;
                var hour = (int)await replyLog.CountDocumentsAsync(r => r.UserId == userId && r.AccountId == accountId && r.AtUtc >= sinceHour && r.Outcome != "Suggested", cancellationToken: ct);
                var day = (int)await replyLog.CountDocumentsAsync(r => r.UserId == userId && r.AccountId == accountId && r.AtUtc >= sinceDay && r.Outcome != "Suggested", cancellationToken: ct);
                var matchedRule = await svc.Db.GetCollection<GmailRule>("gmailRules").Find(r => r.UserId == userId && r.AccountId == accountId && r.Enabled && r.RunAutomatically && r.Actions.Any(a => a.Type == "generateDraft")).ToListAsync(ct)
                    is { Count: > 0 } autoRules && GmailRuleEngine.MatchingRules(autoRules, GmailMessageParser.ToFacts(fetched, cls)).Count > 0;
                var ctx = new GmailReplyContext(fetched.Header("From"), fetched.Labels, doc.ThreadId == doc.MessageId, already, hour, day, now, matchedRule, fetched.Text);
                var confidence = doc.Scores.Count == 0 ? 0.9 : Math.Max(0.5, doc.Scores.Values.Max());
                var decision = GmailReplyPolicy.Decide(cfg.Reply, cfg.Permissions.Profile, cfg.Permissions.Overrides, cls, ctx, confidence);

                await svc.Db.GetCollection<GmailMessageDoc>("gmailMessages").UpdateOneAsync(d => d.Key == doc.Key, Builders<GmailMessageDoc>.Update.Set(d => d.ReplyEvaluated, true), cancellationToken: ct);
                if (decision.Action is GmailReplyAction.Skip or GmailReplyAction.Suggest) continue;

                var body = GmailReplyComposer.Compose(cfg.Reply, DisplayName(fetched.Header("From")), fetched.Header("Subject"), cls.Summary);
                string outcome;
                if (decision.Action == GmailReplyAction.Draft)
                {
                    if (!CanDo(account, cfg, GmailPermission.CreateDrafts)) continue;
                    await CreateReplyDraftAsync(userId, accountId, account.Email, fetched, body, ct);
                    outcome = "Draft";
                }
                else
                {
                    if (!CanDo(account, cfg, GmailPermission.Reply) || !CanDo(account, cfg, GmailPermission.Send)) continue;
                    var (inReplyTo, refs, subject) = GmailServices.ReplyHeaders(fetched);
                    var raw = GmailServices.BuildMime(account.Email, fetched.Header("From"), null, subject, body, inReplyTo, refs);
                    using (await svc.Api(userId, accountId, "users/me/messages/send", HttpMethod.Post, new { raw, threadId = fetched.ThreadId }, ct)) { }
                    outcome = "Sent";
                }
                await replyLog.ReplaceOneAsync(r => r.Key == $"{accountId}:{doc.ThreadId}", new GmailReplyLogEntry { Key = $"{accountId}:{doc.ThreadId}", UserId = userId, AccountId = accountId, ThreadId = doc.ThreadId, Outcome = outcome, AtUtc = DateTime.UtcNow },
                    new ReplaceOptions { IsUpsert = true }, ct);
                await svc.Audit(userId, accountId, outcome == "Sent" ? "autoReply.send" : "autoReply.draft", doc.MessageId, "Automation", "Success", approval: "Approved", ct: CancellationToken.None);
            }
            catch (GmailIntegrationException ex)
            {
                await svc.Audit(userId, accountId, "autoReply", doc.MessageId, "Automation", "Failed", ex.Message, ct: CancellationToken.None);
                await svc.Notify(cfg, "sendFailure", doc.MessageId, "Automatic reply failed", ex.Message, doc.MessageId, doc.ThreadId, ct);
            }
        }
    }

    private static bool CanDo(GmailAccountDoc account, GmailAccountConfig cfg, string permission) =>
        GmailPermissionPolicy.IsAllowed(cfg.Permissions.Profile, cfg.Permissions.Overrides, permission) &&
        (GmailPermission.RequiredScope(permission) is not { } scope || GmailScopes.Satisfies(GmailScopes.Parse(account.GrantedScopes), scope));

    /// <summary>Marks follow-ups due, notifies once, and (when enabled) prepares a draft. Never sends.</summary>
    public async Task SweepFollowUpsAsync(GmailAccountDoc account, GmailAccountConfig cfg, CancellationToken ct)
    {
        if (!cfg.FollowUp.Enabled) return;
        var collection = svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps");
        var now = DateTime.UtcNow;
        var due = await collection.Find(f => f.UserId == cfg.UserId && f.AccountId == account.ConnectionId && f.Status == GmailFollowUpPlanner.AwaitingTheirResponse && f.DueAtUtc <= now
            && (f.SnoozedUntilUtc == null || f.SnoozedUntilUtc <= now)).Limit(50).ToListAsync(ct);
        foreach (var f in due)
        {
            await collection.UpdateOneAsync(x => x.Key == f.Key && x.Status == GmailFollowUpPlanner.AwaitingTheirResponse,
                Builders<GmailFollowUp>.Update.Set(x => x.Status, GmailFollowUpPlanner.FollowUpDue).Set(x => x.UpdatedAtUtc, now), cancellationToken: ct);
            await svc.Notify(cfg, "followUpDue", $"{f.ThreadId}:{f.FollowUpsSent}", "Follow-up due", $"No reply yet: {f.Subject}", f.MessageId, f.ThreadId, ct);
            await svc.Emit(cfg, "no_reply_received", $"{f.ThreadId}:{f.FollowUpsSent}", f.MessageId, f.ThreadId, new() { ["subject"] = f.Subject }, ct);
        }
    }
}
