using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Api.Auth;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record UpdateGmailAccountRequest(string? Nickname, bool? Enabled, bool? IsDefault);
public sealed record ComposeRequest(string? To, string? Cc, string? Subject, string? Body, string? ThreadId);
public sealed record ReplyRequest(string? Body, bool ReplyAll, bool Send);
public sealed record ForwardRequest(string? To, string? Note);
public sealed record ModifyLabelsRequest(string[]? Add, string[]? Remove);
public sealed record LabelRequest(string? Name);
public sealed record FilterRequest(string? From, string? To, string? Subject, string? Query, bool? HasAttachment, string[]? AddLabels, string[]? RemoveLabels, bool Archive, bool MarkRead, bool Star, string? ForwardTo);
public sealed record VacationRequest(bool EnableAutoReply, string? Subject, string? Body, string? StartDate, string? EndDate, bool RestrictToContacts, bool RestrictToDomain);
public sealed record SendAsPatch(string? DisplayName, string? Signature);
public sealed record LanguageRequest(string? DisplayLanguage);
public sealed record CorrectionRequest(string? Category, bool Is = true);
public sealed record RuleTestRequest(GmailRule? Rule, GmailMessageFacts? Sample);
public sealed record RulePreviewRequest(int Max = 25, string? Query = null);
public sealed record RuleRunRequest(string[]? MessageIds, bool Confirm);
public sealed record FollowUpUpdate(string? Status, DateTime? SnoozeUntil, DateTime? DueAt);
public sealed record NotificationsReadRequest(string[]? Keys, bool All);
public sealed record SavedSearchRequest(string? Name, GmailSearchCriteria? Criteria);
public sealed record PubSubEnvelope(PubSubMessage? Message, string? Subscription);
public sealed record PubSubMessage(string? Data, string? MessageId, string? PublishTime);

public static partial class GmailIntegrationModule
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void MapGmailManagement(RouteGroupBuilder root)
    {
        // Pub/Sub push: authenticated by a shared secret in the URL, not by a user session.
        root.MapPost("/push", ReceivePushAsync).AllowAnonymous();

        var g = root.MapGroup("").RequireAuthorization(AuthSetup.UserScheme);

        // ── accounts ────────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/accounts", (GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () => Results.Ok(await ListAccountsAsync(svc, who.UserId, ct))));
        g.MapPatch("/accounts/{id}", (string id, UpdateGmailAccountRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (r.Nickname is { Length: > 60 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["nickname"] = ["The nickname can be at most 60 characters."] });
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, id, ct);
            if (connection is null) return Results.NotFound();
            var accounts = svc.Db.GetCollection<GmailAccountDoc>("gmailAccounts");
            await LoadAccountAsync(svc.Db, connection, ct);
            var update = Builders<GmailAccountDoc>.Update.Set(a => a.UpdatedAtUtc, DateTime.UtcNow);
            if (r.Nickname is not null) update = update.Set(a => a.Nickname, r.Nickname.Trim());
            if (r.Enabled is { } enabled) update = update.Set(a => a.Enabled, enabled);
            await accounts.UpdateOneAsync(a => a.ConnectionId == id && a.UserId == who.UserId, update, cancellationToken: ct);
            if (r.IsDefault == true)
            {
                await accounts.UpdateManyAsync(a => a.UserId == who.UserId, Builders<GmailAccountDoc>.Update.Set(a => a.IsDefault, false), cancellationToken: ct);
                await accounts.UpdateOneAsync(a => a.ConnectionId == id && a.UserId == who.UserId, Builders<GmailAccountDoc>.Update.Set(a => a.IsDefault, true).Set(a => a.Enabled, true), cancellationToken: ct);
            }
            await svc.Audit(who.UserId, id, "account.update", ct: ct);
            return Results.Ok((await ListAccountsAsync(svc, who.UserId, ct)).FirstOrDefault(a => a.Id == id));
        }));
        g.MapPost("/accounts/{id}/test", (string id, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var a = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, id, GmailPermission.Read, ct);
            using var profile = await svc.Api(who.UserId, id, "users/me/profile", ct: ct);
            var p = profile.RootElement;
            return Results.Ok(new
            {
                ok = true, email = p.GetProperty("emailAddress").GetString(),
                messagesTotal = p.TryGetProperty("messagesTotal", out var mt) ? mt.GetInt64() : 0, threadsTotal = p.TryGetProperty("threadsTotal", out var tt) ? tt.GetInt64() : 0,
                scopes = GmailScopes.Parse(a.Account.GrantedScopes),
            });
        }));
        g.MapGet("/accounts/{id}/permissions", (string id, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, id, ct);
            if (connection is null) return Results.NotFound();
            var account = await LoadAccountAsync(svc.Db, connection, ct);
            var cfg = await LoadConfigAsync(svc.Db, who.UserId, id, ct);
            var granted = GmailScopes.Parse(account.GrantedScopes);
            return Results.Ok(new
            {
                profile = cfg.Permissions.Profile,
                scopes = granted.Select(s => new { scope = s, why = GmailScopes.Describe(s) }),
                features = GmailPermission.All.Select(p => new
                {
                    permission = p, label = GmailPermission.Label(p),
                    allowed = GmailPermissionPolicy.IsAllowed(cfg.Permissions.Profile, cfg.Permissions.Overrides, p),
                    requiredScope = GmailPermission.RequiredScope(p),
                    scopeGranted = GmailPermission.RequiredScope(p) is not { } s || GmailScopes.Satisfies(granted, s),
                    why = GmailPermission.RequiredScope(p) is { } sc ? GmailScopes.Describe(sc) : "Daksha-side permission; no extra Google access needed.",
                }),
                upgradeFeatures = GmailScopes.Features.Select(kv => new { feature = kv.Key, scope = kv.Value, why = GmailScopes.Describe(kv.Value), granted = GmailScopes.Satisfies(granted, kv.Value) }),
            });
        }));

        // ── configuration ──────────────────────────────────────────────────────────────────────────────
        g.MapGet("/config", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            return connection is null ? Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.") : Results.Ok(await LoadConfigAsync(svc.Db, who.UserId, connection.Id, ct));
        }));
        g.MapPut("/config", (string? accountId, GmailAccountConfig body, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var errors = GmailConfigValidator.Validate(body);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var configs = svc.Db.GetCollection<GmailAccountConfig>("gmailAccountConfigs");
            var current = await configs.Find(c => c.ConnectionId == connection.Id && c.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (current is not null && body.Version != current.Version)
                return Results.Problem(statusCode: 409, title: "These settings changed elsewhere. Reload and try again.", extensions: new Dictionary<string, object?> { ["code"] = "version_conflict" });
            var expected = current?.Version ?? 0;
            body.ConnectionId = connection.Id; body.UserId = who.UserId; body.Version = expected + 1; body.UpdatedAtUtc = DateTime.UtcNow;
            var result = await configs.ReplaceOneAsync(c => c.ConnectionId == connection.Id && c.UserId == who.UserId && (current == null || c.Version == expected), body, new ReplaceOptions { IsUpsert = current is null }, ct);
            if (current is not null && result.MatchedCount == 0) return Results.Problem(statusCode: 409, title: "These settings changed elsewhere. Reload and try again.", extensions: new Dictionary<string, object?> { ["code"] = "version_conflict" });
            var detail = current is null || current.Permissions.Profile != body.Permissions.Profile ? $"config.update (profile {body.Permissions.Profile})" : "config.update";
            await svc.Audit(who.UserId, connection.Id, detail, ct: ct);
            return Results.Ok(body);
        }));

        // ── messages ───────────────────────────────────────────────────────────────────────────────────
        g.MapPost("/messages/{id}/actions/{action}", (string id, string action, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (action is "trash" or "untrash")
            {
                var t = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.DeleteMessages, ct);
                using var _ = await svc.Api(who.UserId, t.Connection.Id, $"users/me/messages/{Uri.EscapeDataString(id)}/{action}", HttpMethod.Post, null, ct);
                await svc.Audit(who.UserId, t.Connection.Id, $"message.{action}", id, ct: ct);
                return Results.Ok(new { id, action });
            }
            if (!GmailServices.LabelActions.TryGetValue(action, out var spec)) return Results.NotFound();
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, spec.Permission, ct);
            await svc.ModifyLabels(who.UserId, auth.Connection.Id, id, spec.Add, spec.Remove, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, $"message.{action}", id, ct: ct);
            return Results.Ok(new { id, action });
        }));
        g.MapPost("/messages/{id}/labels", (string id, string? accountId, ModifyLabelsRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ApplyLabels, ct);
            var add = (r.Add ?? []).Take(20).ToArray(); var remove = (r.Remove ?? []).Take(20).ToArray();
            if (add.Length + remove.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["labels"] = ["Choose at least one label to add or remove."] });
            await svc.ModifyLabels(who.UserId, auth.Connection.Id, id, add, remove, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "message.labels", id, ct: ct);
            return Results.Ok(new { id });
        }));
        g.MapDelete("/messages/{id}", (string id, string? accountId, bool confirm, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (!confirm) return Results.Problem(statusCode: 409, title: "Permanent deletion can't be undone. Confirm to continue.", extensions: new Dictionary<string, object?> { ["code"] = "confirmation_required" });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.PermanentDelete, ct);
            using var _ = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/messages/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);
            await svc.Db.GetCollection<GmailMessageDoc>("gmailMessages").DeleteOneAsync(d => d.Key == $"{auth.Connection.Id}:{id}", ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "message.permanentDelete", id, approval: "Approved", ct: ct);
            return Results.Ok(new { id });
        }));
        g.MapPost("/messages/{id}/reply", (string id, string? accountId, ReplyRequest r, GmailServices svc, GmailPipeline pipe, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (string.IsNullOrWhiteSpace(r.Body) || r.Body.Length > 100_000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Enter a reply up to 100,000 characters."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, r.Send ? GmailPermission.Reply : GmailPermission.CreateDrafts, ct);
            var original = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            var body = AppendSignature(r.Body, auth.Config.Reply.Signature);
            if (!r.Send)
            {
                var draftId = await pipe.CreateReplyDraftAsync(who.UserId, auth.Connection.Id, auth.Account.Email, original, body, ct);
                await svc.Audit(who.UserId, auth.Connection.Id, "reply.draft", id, ct: ct);
                return Results.Ok(new { draftId });
            }
            var (inReplyTo, refs, subject) = GmailServices.ReplyHeaders(original);
            var to = string.IsNullOrWhiteSpace(original.Header("Reply-To")) ? original.Header("From") : original.Header("Reply-To");
            var cc = r.ReplyAll ? string.Join(", ", new[] { original.Header("To"), original.Header("Cc") }.Where(x => x.Length > 0)) : null;
            var raw = GmailServices.BuildMime(auth.Account.Email, to, RemoveSelf(cc, auth.Account.Email), subject, body, inReplyTo, refs);
            using var sent = await svc.Api(who.UserId, auth.Connection.Id, "users/me/messages/send", HttpMethod.Post, new { raw, threadId = original.ThreadId }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "reply.send", id, ct: ct);
            return Results.Ok(new { messageId = sent.RootElement.TryGetProperty("id", out var mid) ? mid.GetString() : null });
        }));
        g.MapPost("/messages/{id}/forward", (string id, string? accountId, ForwardRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (string.IsNullOrWhiteSpace(r.To) || r.To.Length > 1000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Enter one or more recipients."] });
            try { MimeKit.InternetAddressList.Parse(r.To); } catch (MimeKit.ParseException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["Enter valid email addresses."] }); }
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Forward, ct);
            var m = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            var note = string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note.Trim() + "\n\n";
            var body = $"{note}---------- Forwarded message ----------\nFrom: {m.Header("From")}\nDate: {m.Date:R}\nSubject: {m.Header("Subject")}\nTo: {m.Header("To")}\n\n{m.Text}";
            var raw = GmailServices.BuildMime(auth.Account.Email, r.To, null, (m.Header("Subject").StartsWith("fwd:", StringComparison.OrdinalIgnoreCase) ? "" : "Fwd: ") + m.Header("Subject"), body);
            using var sent = await svc.Api(who.UserId, auth.Connection.Id, "users/me/messages/send", HttpMethod.Post, new { raw }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "message.forward", id, ct: ct);
            return Results.Ok(new { messageId = sent.RootElement.TryGetProperty("id", out var mid) ? mid.GetString() : null, attachmentsIncluded = false });
        }));
        g.MapPost("/search/build", (GmailSearchCriteria c) => Results.Ok(new { query = GmailSearchBuilder.Build(c) }));
        g.MapGet("/saved-searches", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
            Results.Ok(await svc.Db.GetCollection<GmailSavedSearch>("gmailSavedSearches").Find(s => s.UserId == who.UserId).SortBy(s => s.Name).ToListAsync(ct))));
        g.MapPost("/saved-searches", (string? accountId, SavedSearchRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 || r.Criteria is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name the search (up to 80 characters)."] });
            var query = GmailSearchBuilder.Build(r.Criteria);
            if (query.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["criteria"] = ["Add at least one search condition."] });
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            var doc = new GmailSavedSearch { UserId = who.UserId, AccountId = connection?.Id ?? "", Name = r.Name.Trim(), Criteria = r.Criteria, Query = query, CreatedAtUtc = DateTime.UtcNow };
            await svc.Db.GetCollection<GmailSavedSearch>("gmailSavedSearches").InsertOneAsync(doc, cancellationToken: ct);
            return Results.Ok(doc);
        }));
        g.MapDelete("/saved-searches/{id}", (string id, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
            (await svc.Db.GetCollection<GmailSavedSearch>("gmailSavedSearches").DeleteOneAsync(s => s.Id == id && s.UserId == who.UserId, ct)).DeletedCount > 0 ? Results.NoContent() : Results.NotFound()));

        // ── threads ────────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/threads", (string? q, string? pageToken, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (q is { Length: > 500 } || pageToken is { Length: > 2048 }) return Results.BadRequest("The Gmail query or page token is too long.");
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Search, ct);
            var p = new Dictionary<string, string?> { ["maxResults"] = "20", ["q"] = string.IsNullOrWhiteSpace(q) ? "in:inbox" : q.Trim() };
            if (!string.IsNullOrWhiteSpace(pageToken)) p["pageToken"] = pageToken;
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, QueryHelpers.AddQueryString("users/me/threads", p), ct: ct);
            var threads = doc.RootElement.TryGetProperty("threads", out var arr) ? arr.EnumerateArray().Select(t => new { id = t.GetProperty("id").GetString(), snippet = t.TryGetProperty("snippet", out var s) ? s.GetString() : "" }).ToArray() : [];
            return Results.Ok(new { threads, nextPageToken = doc.RootElement.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null });
        }));
        g.MapGet("/threads/{id}", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            var messages = await FetchThreadAsync(svc, who.UserId, auth.Connection.Id, id, ct);
            return Results.Ok(new { id, messages = messages.Select(m => new { m.Id, from = m.Header("From"), to = m.Header("To"), cc = m.Header("Cc"), subject = m.Header("Subject"), date = m.Date, text = m.Text.Length > 50_000 ? m.Text[..50_000] : m.Text, html = m.Html, labels = m.Labels, attachments = m.Attachments.Count }) });
        }));
        g.MapPost("/threads/{id}/summarise", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            if (!(auth.Config.Ai.Enabled && auth.Config.Ai.Features["longThreadSummary"].Enabled)) return Results.Problem(statusCode: 409, title: "Conversation summaries are turned off in AI settings.", extensions: new Dictionary<string, object?> { ["code"] = "feature_disabled" });
            var messages = await FetchThreadAsync(svc, who.UserId, auth.Connection.Id, id, ct);
            return Results.Ok(SummariseThread(messages, auth.Config.Ai.SummaryLength));
        }));
        g.MapPost("/threads/{id}/reply-draft", (string id, string? accountId, GmailServices svc, GmailPipeline pipe, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateDrafts, ct);
            var messages = await FetchThreadAsync(svc, who.UserId, auth.Connection.Id, id, ct);
            var last = messages.LastOrDefault(m => !m.Labels.Contains("SENT")) ?? messages.Last();
            var cls = GmailIntelligence.Classify(GmailMessageParser.ToInput(last), null, auth.Config.Ai.SummaryLength);
            var body = GmailReplyComposer.Compose(auth.Config.Reply, GmailPipeline.DisplayName(last.Header("From")), last.Header("Subject"), cls.Summary);
            var draftId = await pipe.CreateReplyDraftAsync(who.UserId, auth.Connection.Id, auth.Account.Email, last, body, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "reply.draft", last.Id, ct: ct);
            return Results.Ok(new { draftId, body });
        }));

        // ── drafts ─────────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/drafts", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            using var list = await svc.Api(who.UserId, auth.Connection.Id, "users/me/drafts?maxResults=20", ct: ct);
            var ids = list.RootElement.TryGetProperty("drafts", out var arr) ? arr.EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList() : [];
            var drafts = new List<object>();
            foreach (var did in ids)
            {
                try { drafts.Add(await GetDraftAsync(svc, who.UserId, auth.Connection.Id, did, ct)); } catch (GmailIntegrationException ex) when (ex.StatusCode == 404) { }
            }
            return Results.Ok(new { drafts });
        }));
        g.MapGet("/drafts/{id}", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            return Results.Ok(await GetDraftAsync(svc, who.UserId, auth.Connection.Id, id, ct));
        }));
        g.MapPost("/drafts", (string? accountId, ComposeRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateDrafts, ct);
            if (ValidateCompose(r, requireRecipient: false) is { } bad) return bad;
            var raw = GmailServices.BuildMime(auth.Account.Email, string.IsNullOrWhiteSpace(r.To) ? auth.Account.Email : r.To, r.Cc, r.Subject ?? "", AppendSignature(r.Body ?? "", auth.Config.Reply.Signature));
            using var created = await svc.Api(who.UserId, auth.Connection.Id, "users/me/drafts", HttpMethod.Post, new { message = new { raw, threadId = r.ThreadId } }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "draft.create", ct: ct);
            return Results.Ok(new { draftId = created.RootElement.GetProperty("id").GetString() });
        }));
        g.MapPut("/drafts/{id}", (string id, string? accountId, ComposeRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateDrafts, ct);
            if (ValidateCompose(r, requireRecipient: false) is { } bad) return bad;
            var raw = GmailServices.BuildMime(auth.Account.Email, string.IsNullOrWhiteSpace(r.To) ? auth.Account.Email : r.To, r.Cc, r.Subject ?? "", r.Body ?? "");
            using var updated = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/drafts/{Uri.EscapeDataString(id)}", HttpMethod.Put, new { message = new { raw, threadId = r.ThreadId } }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "draft.update", id, ct: ct);
            return Results.Ok(new { draftId = id });
        }));
        g.MapDelete("/drafts/{id}", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateDrafts, ct);
            using var _ = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/drafts/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "draft.delete", id, ct: ct);
            return Results.NoContent();
        }));
        g.MapPost("/drafts/{id}/send", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Send, ct);
            using var sent = await svc.Api(who.UserId, auth.Connection.Id, "users/me/drafts/send", HttpMethod.Post, new { id }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "draft.send", id, ct: ct);
            return Results.Ok(new { messageId = sent.RootElement.TryGetProperty("id", out var mid) ? mid.GetString() : null });
        }));

        // ── labels & filters ───────────────────────────────────────────────────────────────────────────
        g.MapGet("/labels", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, "users/me/labels", ct: ct);
            var labels = doc.RootElement.TryGetProperty("labels", out var arr)
                ? arr.EnumerateArray().Select(l => new { id = l.GetProperty("id").GetString(), name = l.GetProperty("name").GetString(), type = l.TryGetProperty("type", out var t) ? t.GetString() : "user" }).OrderBy(l => l.type == "system" ? 0 : 1).ThenBy(l => l.name).ToArray() : [];
            return Results.Ok(new { labels });
        }));
        g.MapPost("/labels", (string? accountId, LabelRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (LabelNameError(r.Name) is { } err) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [err] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateLabels, ct);
            using var created = await svc.Api(who.UserId, auth.Connection.Id, "users/me/labels", HttpMethod.Post, new { name = r.Name!.Trim(), labelListVisibility = "labelShow", messageListVisibility = "show" }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "label.create", r.Name, ct: ct);
            return Results.Ok(new { id = created.RootElement.GetProperty("id").GetString(), name = created.RootElement.GetProperty("name").GetString() });
        }));
        g.MapPut("/labels/{id}", (string id, string? accountId, LabelRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (LabelNameError(r.Name) is { } err) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [err] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateLabels, ct);
            if (IsSystemLabel(id)) return Results.Problem(statusCode: 409, title: "Gmail's built-in labels can't be renamed.");
            using var updated = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/labels/{Uri.EscapeDataString(id)}", HttpMethod.Put, new { id, name = r.Name!.Trim() }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "label.update", id, ct: ct);
            return Results.Ok(new { id, name = r.Name.Trim() });
        }));
        g.MapDelete("/labels/{id}", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateLabels, ct);
            if (IsSystemLabel(id)) return Results.Problem(statusCode: 409, title: "Gmail's built-in labels can't be deleted.");
            using var _ = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/labels/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "label.delete", id, ct: ct);
            return Results.NoContent();
        }));
        g.MapGet("/filters", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ManageSettings, ct);
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, "users/me/settings/filters", ct: ct);
            return Results.Ok(new { filters = doc.RootElement.TryGetProperty("filter", out var arr) ? JsonSerializer.Deserialize<JsonElement>(arr.GetRawText()) : JsonSerializer.Deserialize<JsonElement>("[]") });
        }));
        g.MapPost("/filters", (string? accountId, FilterRequest r, GmailServices svc, GmailPipeline pipe, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (string.IsNullOrWhiteSpace(r.From) && string.IsNullOrWhiteSpace(r.To) && string.IsNullOrWhiteSpace(r.Subject) && string.IsNullOrWhiteSpace(r.Query) && r.HasAttachment != true)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["criteria"] = ["Add at least one condition to the filter."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateFilters, ct);
            var add = new List<string>(r.AddLabels ?? []); var remove = new List<string>(r.RemoveLabels ?? []);
            if (r.Archive) remove.Add("INBOX"); if (r.MarkRead) remove.Add("UNREAD"); if (r.Star) add.Add("STARRED");
            if (add.Count + remove.Count == 0 && string.IsNullOrWhiteSpace(r.ForwardTo)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["action"] = ["Choose what the filter should do."] });
            if (!string.IsNullOrWhiteSpace(r.ForwardTo) && !await pipe.IsVerifiedForwardingAddressAsync(who.UserId, auth.Connection.Id, r.ForwardTo, ct))
                return Results.Problem(statusCode: 409, title: $"{r.ForwardTo} isn't a verified forwarding address on this Gmail account.");
            var body = new { criteria = new { from = r.From, to = r.To, subject = r.Subject, query = r.Query, hasAttachment = r.HasAttachment }, action = new { addLabelIds = add.Distinct().ToArray(), removeLabelIds = remove.Distinct().ToArray(), forward = string.IsNullOrWhiteSpace(r.ForwardTo) ? null : r.ForwardTo } };
            using var created = await svc.Api(who.UserId, auth.Connection.Id, "users/me/settings/filters", HttpMethod.Post, body, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "filter.create", created.RootElement.TryGetProperty("id", out var fid) ? fid.GetString() : null, ct: ct);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(created.RootElement.GetRawText()));
        }));
        g.MapDelete("/filters/{id}", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.CreateFilters, ct);
            using var _ = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/settings/filters/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "filter.delete", id, ct: ct);
            return Results.NoContent();
        }));
        g.MapPost("/filters/test", (string? accountId, GmailSearchCriteria c, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var query = GmailSearchBuilder.Build(c);
            if (query.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["criteria"] = ["Add at least one condition to test."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Search, ct);
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, QueryHelpers.AddQueryString("users/me/messages", new Dictionary<string, string?> { ["q"] = query, ["maxResults"] = "10" }), ct: ct);
            var ids = doc.RootElement.TryGetProperty("messages", out var arr) ? arr.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray() : [];
            return Results.Ok(new { query, sampleCount = ids.Length, estimate = doc.RootElement.TryGetProperty("resultSizeEstimate", out var est) ? est.GetInt64() : ids.Length, sampleIds = ids });
        }));

        // ── Gmail account settings (what the API really supports) ──────────────────────────────────────────
        g.MapGet("/account-settings", (string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ManageSettings, ct);
            async Task<object> Section(string path, string? note = null, bool canEdit = false)
            {
                try
                {
                    using var doc = await svc.Api(who.UserId, auth.Connection.Id, path, ct: ct);
                    return new { supported = true, editable = canEdit, value = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText()), note };
                }
                catch (GmailIntegrationException ex) { return new { supported = true, editable = false, value = (JsonElement?)null, note = ex.Message }; }
            }
            return Results.Ok(new
            {
                vacation = await Section("users/me/settings/vacation", canEdit: true),
                language = await Section("users/me/settings/language", canEdit: true),
                sendAs = await Section("users/me/settings/sendAs", "Send-as aliases and the per-alias signature. Display name and signature are editable.", true),
                forwardingAddresses = await Section("users/me/settings/forwardingAddresses", "Listed only. Adding or verifying a forwarding address needs Google's restricted gmail.settings.sharing scope, which Daksha doesn't request.", false),
                autoForwarding = await Section("users/me/settings/autoForwarding", "Read-only for the same reason as forwarding addresses.", false),
                imap = await Section("users/me/settings/imap", "Read-only. Change IMAP in Gmail → Settings → Forwarding and POP/IMAP.", false),
                pop = await Section("users/me/settings/pop", "Read-only. Change POP in Gmail → Settings → Forwarding and POP/IMAP.", false),
                notSupported = new[]
                {
                    new { setting = "Signature (non-API)", reason = "The signature lives on each send-as alias and is editable above. Mobile signatures are not exposed by the API.", url = (string?)null },
                    new { setting = "Inbox type, themes, keyboard shortcuts, conversation view, undo-send window", reason = "The Gmail API has no resource for these. Change them in Gmail → Settings.", url = (string?)"https://mail.google.com/mail/u/0/#settings/general" },
                },
            });
        }));
        g.MapPut("/account-settings/vacation", (string? accountId, VacationRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var errors = new Dictionary<string, string[]>();
            if (r.Subject is { Length: > 255 }) errors["subject"] = ["The subject can be at most 255 characters."];
            if (r.Body is { Length: > 10_000 }) errors["body"] = ["The message can be at most 10,000 characters."];
            if (r.EnableAutoReply && string.IsNullOrWhiteSpace(r.Body)) errors["body"] = ["Write the auto-reply message."];
            DateTimeOffset? start = null, end = null;
            if (!string.IsNullOrWhiteSpace(r.StartDate)) { if (DateTimeOffset.TryParse(r.StartDate, out var s)) start = s; else errors["startDate"] = ["Enter a valid start date."]; }
            if (!string.IsNullOrWhiteSpace(r.EndDate)) { if (DateTimeOffset.TryParse(r.EndDate, out var e)) end = e; else errors["endDate"] = ["Enter a valid end date."]; }
            if (start is not null && end is not null && end <= start) errors["endDate"] = ["The end date must be after the start date."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ManageSettings, ct);
            var body = new { enableAutoReply = r.EnableAutoReply, responseSubject = r.Subject, responseBodyPlainText = r.Body, restrictToContacts = r.RestrictToContacts, restrictToDomain = r.RestrictToDomain,
                startTime = start?.ToUnixTimeMilliseconds().ToString(), endTime = end?.ToUnixTimeMilliseconds().ToString() };
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, "users/me/settings/vacation", HttpMethod.Put, body, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "settings.vacation", approval: "Approved", ct: ct);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText()));
        }));
        g.MapPut("/account-settings/language", (string? accountId, LanguageRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (string.IsNullOrWhiteSpace(r.DisplayLanguage) || r.DisplayLanguage.Length > 20) return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayLanguage"] = ["Enter a language code such as en or hi."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ManageSettings, ct);
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, "users/me/settings/language", HttpMethod.Put, new { displayLanguage = r.DisplayLanguage.Trim() }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "settings.language", ct: ct);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText()));
        }));
        g.MapPatch("/account-settings/send-as/{email}", (string email, string? accountId, SendAsPatch r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (r.Signature is { Length: > 10_000 } || r.DisplayName is { Length: > 100 }) return Results.ValidationProblem(new Dictionary<string, string[]> { ["signature"] = ["The signature or display name is too long."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ManageSettings, ct);
            using var doc = await svc.Api(who.UserId, auth.Connection.Id, $"users/me/settings/sendAs/{Uri.EscapeDataString(email)}", HttpMethod.Patch, new { displayName = r.DisplayName, signature = r.Signature }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "settings.sendAs", email, ct: ct);
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText()));
        }));

        // ── attachments ────────────────────────────────────────────────────────────────────────────────
        g.MapGet("/messages/{id}/attachments", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ReadAttachments, ct);
            if (!auth.Config.Attachments.Enabled) return Results.Problem(statusCode: 409, title: "Attachment processing is turned off in Gmail settings.", extensions: new Dictionary<string, object?> { ["code"] = "feature_disabled" });
            var m = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            return Results.Ok(new
            {
                attachments = m.Attachments.Select(a =>
                {
                    var name = GmailAttachmentSafety.SafeFileName(a.FileName);
                    var byName = GmailAttachmentSafety.Inspect(a.FileName, [], auth.Config.Attachments, a.Size); // name + size only; bytes are checked on download
                    return new { a.AttachmentId, fileName = name, a.MimeType, a.Size, blocked = byName.Blocked, reason = byName.Reason, documentType = GmailAttachmentSafety.ClassifyDocument(name) };
                }),
            });
        }));
        g.MapGet("/messages/{id}/attachments/{attachmentId}", (string id, string attachmentId, string? accountId, HttpContext http, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.ReadAttachments, ct);
            if (!auth.Config.Attachments.Enabled) return Results.Problem(statusCode: 409, title: "Attachment processing is turned off in Gmail settings.");
            var m = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            var att = m.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
            if (att is null) return Results.NotFound();
            var preCheck = GmailAttachmentSafety.Inspect(att.FileName, [], auth.Config.Attachments, att.Size);
            if (preCheck.Blocked) { await svc.Audit(who.UserId, auth.Connection.Id, "attachment.download", id, status: "Denied", error: preCheck.Reason, ct: ct); return Results.Problem(statusCode: 422, title: preCheck.Reason ?? "This attachment is blocked."); }
            var bytes = await svc.AttachmentBytes(who.UserId, auth.Connection.Id, id, attachmentId, ct);
            var (check, _, _, _) = await svc.InspectAttachment(who.UserId, auth.Connection.Id, auth.Config, id, att, bytes, ct);
            if (check.Blocked) { await svc.Audit(who.UserId, auth.Connection.Id, "attachment.download", id, status: "Denied", error: check.Reason, ct: ct); return Results.Problem(statusCode: 422, title: check.Reason ?? "This attachment is blocked."); }
            await svc.Audit(who.UserId, auth.Connection.Id, "attachment.download", id, ct: ct);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "sandbox";
            return Results.File(bytes, "application/octet-stream", check.SafeFileName); // always a download, never rendered inline
        }));

        // ── AI ─────────────────────────────────────────────────────────────────────────────────────────
        g.MapPost("/messages/{id}/analyze", (string id, string? accountId, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            if (!auth.Config.Ai.Enabled) return Results.Problem(statusCode: 409, title: "AI email intelligence is turned off for this account.", extensions: new Dictionary<string, object?> { ["code"] = "feature_disabled" });
            var m = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            var sender = GmailIntelligence.ExtractAddress(m.Header("From"));
            var corrections = await svc.Db.GetCollection<GmailCorrection>("gmailCorrections").Find(c => c.UserId == who.UserId && c.AccountId == auth.Connection.Id && c.Sender == sender).ToListAsync(ct);
            var cls = GmailIntelligence.Classify(GmailMessageParser.ToInput(m), corrections, auth.Config.Ai.SummaryLength);
            var categories = GmailIntelligence.EffectiveCategories(cls, auth.Config.Ai);
            var ctx = new GmailReplyContext(m.Header("From"), m.Labels, m.ThreadId == m.Id, false, 0, 0, DateTimeOffset.UtcNow, false, m.Text);
            var decision = GmailReplyPolicy.Decide(auth.Config.Reply, auth.Config.Permissions.Profile, auth.Config.Permissions.Overrides, cls, ctx, cls.Scores.Count == 0 ? 0.9 : Math.Max(0.5, cls.Scores.Values.Max()));
            await svc.Audit(who.UserId, auth.Connection.Id, "ai.analyze", id, ct: ct);
            return Results.Ok(new
            {
                messageId = id, categories, scores = cls.Scores, cls.Important, cls.Urgent, cls.Suspicious, cls.NeedsReply, cls.Automated, cls.Sentiment, cls.Summary,
                cls.ActionItems, deadlines = cls.Deadlines.Select(d => new { date = d.Date, d.Context }), cls.Decisions, cls.Contacts, correctedBy = cls.CorrectedBy,
                replyDecision = new { action = decision.Action.ToString(), decision.Reason },
                suggestedReply = decision.Action == GmailReplyAction.Skip ? null : GmailReplyComposer.Compose(auth.Config.Reply, GmailPipeline.DisplayName(m.Header("From")), m.Header("Subject"), cls.Summary),
                method = "local-heuristics", externalProviderUsed = false,
            });
        }));
        g.MapPost("/messages/{id}/classification/correct", (string id, string? accountId, CorrectionRequest r, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            if (r.Category is null || !GmailIntelligence.FeatureIds.Contains(r.Category)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Choose a valid category."] });
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            var m = await svc.Fetch(who.UserId, auth.Connection.Id, id, ct);
            var sender = GmailIntelligence.ExtractAddress(m.Header("From"));
            await svc.Db.GetCollection<GmailCorrection>("gmailCorrections").ReplaceOneAsync(c => c.UserId == who.UserId && c.AccountId == auth.Connection.Id && c.Sender == sender && c.Category == r.Category,
                new GmailCorrection { UserId = who.UserId, AccountId = auth.Connection.Id, Sender = sender, Category = r.Category, Is = r.Is, CreatedAtUtc = DateTime.UtcNow }, new ReplaceOptions { IsUpsert = true }, ct);
            await svc.Audit(who.UserId, auth.Connection.Id, "ai.correct", id, ct: ct);
            return Results.Ok(new { sender, category = r.Category, r.Is, appliesTo = "future messages from this sender" });
        }));
        g.MapGet("/analysis", (string? accountId, string? category, bool? needsReply, int? take, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var connection = await ResolveConnectionAsync(svc.Db, who.UserId, accountId, ct);
            if (connection is null) return Results.Problem(statusCode: 409, title: "Connect a Gmail account to continue.");
            var f = Builders<GmailMessageDoc>.Filter.Eq(d => d.UserId, who.UserId) & Builders<GmailMessageDoc>.Filter.Eq(d => d.AccountId, connection.Id) & Builders<GmailMessageDoc>.Filter.Eq(d => d.Processed, true);
            if (!string.IsNullOrWhiteSpace(category)) f &= Builders<GmailMessageDoc>.Filter.AnyEq(d => d.Categories, category);
            if (needsReply == true) f &= Builders<GmailMessageDoc>.Filter.Eq(d => d.NeedsReply, true);
            var items = await svc.Db.GetCollection<GmailMessageDoc>("gmailMessages").Find(f).SortByDescending(d => d.DateUtc).Limit(Math.Clamp(take ?? 25, 1, 100)).ToListAsync(ct);
            return Results.Ok(new { items = items.Select(d => new { d.MessageId, d.ThreadId, d.From, d.Subject, date = d.DateUtc, d.Categories, d.Important, d.Urgent, d.Suspicious, d.NeedsReply, d.Summary, d.ActionItems, deadlines = d.Deadlines, d.IsDuplicate }) });
        }));

        // ── rules ──────────────────────────────────────────────────────────────────────────────────────
        MapRules(g);
        MapOperations(g);
        MapDashboard(g);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Maps Gmail/permission failures to safe responses. Gmail's own 401 becomes 409 so it can't be mistaken for a Daksha session expiry.</summary>
    internal static async Task<IResult> Run(GmailServices svc, Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (GmailIntegrationException ex)
        {
            var status = ex.StatusCode == 401 ? 409 : ex.StatusCode;
            var code = ex.StatusCode == 401 ? "gmail_reauth_required" : ex.Code;
            return Results.Problem(statusCode: status, title: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = code, ["requiredScope"] = ex.RequiredScope });
        }
        catch (HttpRequestException ex)
        {
            svc.Log.LogError(ex, "Gmail request failed");
            return Results.Problem(statusCode: 502, title: "Could not reach Gmail. Please try again.");
        }
        catch (JsonException ex)
        {
            svc.Log.LogError(ex, "Gmail returned a response Daksha could not read");
            return Results.Problem(statusCode: 502, title: "Gmail returned a response Daksha couldn't read.");
        }
    }

    private sealed record AccountDto(string Id, string Email, string Nickname, bool IsDefault, bool Enabled, bool IsWorkspace, string Status, string? StatusDetail,
        string[] Scopes, string SyncStatus, DateTime? LastSuccessUtc, DateTime? LastFailureUtc, string? LastError, long MessagesSynced, DateTime? WatchExpiresUtc, string Profile);

    private static async Task<List<AccountDto>> ListAccountsAsync(GmailServices svc, string userId, CancellationToken ct)
    {
        var connections = await svc.Db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.UserId == userId && c.IntegrationKey == IntegrationKey && c.Status != ConnectionStatus.Disconnected)
            .SortBy(c => c.CreatedAt).ToListAsync(ct);
        var result = new List<AccountDto>();
        foreach (var c in connections)
        {
            var account = await LoadAccountAsync(svc.Db, c, ct);
            var state = await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").Find(s => s.AccountId == c.Id).FirstOrDefaultAsync(ct);
            var cfg = await LoadConfigAsync(svc.Db, userId, c.Id, ct);
            var (status, detail) = AccountStatus(c, account, state);
            result.Add(new AccountDto(c.Id, account.Email.Length > 0 ? account.Email : c.AccountIdentifier ?? "", account.Nickname, account.IsDefault, account.Enabled, account.IsWorkspace, status, detail,
                GmailScopes.Parse(account.GrantedScopes), state?.Status ?? "Idle", state?.LastSuccessUtc, state?.LastFailureUtc, state?.LastError, state?.MessagesSynced ?? 0, state?.WatchExpiresUtc, cfg.Permissions.Profile));
        }
        return result;
    }

    /// <summary>One of: Connected, Connecting, Synchronising, ConnectionExpired, PermissionRevoked, AuthenticationFailed, SyncFailed, Disabled, Disconnected.</summary>
    public static (string Status, string? Detail) AccountStatus(IntegrationConnection c, GmailAccountDoc account, GmailSyncState? state)
    {
        if (c.Status == ConnectionStatus.Disconnected) return ("Disconnected", "Reconnect Gmail to use this account.");
        if (!account.Enabled) return ("Disabled", "This account is switched off in Daksha.");
        if (c.Status == ConnectionStatus.Error)
            return c.LastError?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true ? ("ConnectionExpired", "Google access expired or was revoked. Reconnect this account.") : ("AuthenticationFailed", c.LastError ?? "Reconnect this account.");
        if (c.Status != ConnectionStatus.Connected) return ("Connecting", null);
        if (state?.Status == "Synchronising") return ("Synchronising", null);
        if (state?.Status == "Failed")
            return state.LastError?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true ? ("PermissionRevoked", "A needed Google permission is missing. Reconnect and approve it.") : ("SyncFailed", state.LastError);
        return ("Connected", null);
    }

    private static string AppendSignature(string body, string signature) =>
        string.IsNullOrWhiteSpace(signature) ? body : $"{body}\r\n\r\n-- \r\n{signature.Trim()}";

    private static string? RemoveSelf(string? addresses, string self)
    {
        if (string.IsNullOrWhiteSpace(addresses)) return null;
        try
        {
            var list = MimeKit.InternetAddressList.Parse(addresses);
            var kept = list.Mailboxes.Where(m => !string.Equals(m.Address, self, StringComparison.OrdinalIgnoreCase)).Select(m => m.ToString()).ToList();
            return kept.Count == 0 ? null : string.Join(", ", kept);
        }
        catch (MimeKit.ParseException) { return null; }
    }

    private static IResult? ValidateCompose(ComposeRequest r, bool requireRecipient)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Subject is { Length: > 998 } || r.Subject?.Contains('\n') == true || r.Subject?.Contains('\r') == true) errors["subject"] = ["Enter a subject without line breaks."];
        if (r.Body is { Length: > 100_000 }) errors["body"] = ["Messages can be at most 100,000 characters."];
        foreach (var (field, value) in new[] { ("to", r.To), ("cc", r.Cc) })
            if (!string.IsNullOrWhiteSpace(value))
                try { MimeKit.InternetAddressList.Parse(value); } catch (MimeKit.ParseException) { errors[field] = ["Enter valid email addresses."]; }
        if (requireRecipient && string.IsNullOrWhiteSpace(r.To)) errors["to"] = ["Enter one or more recipients."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static string? LabelNameError(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 225 ? "Enter a label name up to 225 characters." : name.Any(char.IsControl) ? "Label names can't contain control characters." : null;

    private static readonly HashSet<string> SystemLabels = ["INBOX", "SENT", "DRAFT", "TRASH", "SPAM", "STARRED", "IMPORTANT", "UNREAD", "CHAT", "CATEGORY_PERSONAL", "CATEGORY_SOCIAL", "CATEGORY_PROMOTIONS", "CATEGORY_UPDATES", "CATEGORY_FORUMS"];
    private static bool IsSystemLabel(string id) => SystemLabels.Contains(id);

    private static async Task<List<GmailFetched>> FetchThreadAsync(GmailServices svc, string userId, string accountId, string threadId, CancellationToken ct)
    {
        using var doc = await svc.Api(userId, accountId, $"users/me/threads/{Uri.EscapeDataString(threadId)}?format=full", ct: ct);
        return doc.RootElement.TryGetProperty("messages", out var arr) ? arr.EnumerateArray().Select(GmailMessageParser.Parse).OrderBy(m => m.Date).ToList() : [];
    }

    public static object SummariseThread(IReadOnlyList<GmailFetched> messages, string length)
    {
        var participants = messages.SelectMany(m => new[] { m.Header("From"), m.Header("To") }).Select(GmailIntelligence.ExtractAddress).Where(a => a.Contains('@')).Distinct().ToList();
        var perMessage = messages.Select(m => $"{(GmailPipeline.DisplayName(m.Header("From")) is { Length: > 0 } n ? n : "Someone")}: {GmailIntelligence.Summarise(m.Header("Subject"), GmailPromptGuard.StripInjection(m.Text), "short")}").ToList();
        var all = string.Join("\n\n", messages.Select(m => GmailPromptGuard.StripInjection(m.Text)));
        var cls = GmailIntelligence.Classify(new GmailMessageInput(messages.LastOrDefault()?.Header("From") ?? "", "", messages.FirstOrDefault()?.Header("Subject") ?? "", all, messages.LastOrDefault()?.Date ?? DateTimeOffset.UtcNow, [], false, false, false, false), null, length);
        return new
        {
            subject = messages.FirstOrDefault()?.Header("Subject") ?? "", messageCount = messages.Count, participants,
            summary = length == "short" ? perMessage.LastOrDefault() ?? "" : string.Join("\n", perMessage.TakeLast(length == "detailed" ? 10 : 4)),
            actionItems = cls.ActionItems, deadlines = cls.Deadlines.Select(d => new { date = d.Date, d.Context }), decisions = cls.Decisions, method = "local-heuristics",
        };
    }

    private static async Task<object> GetDraftAsync(GmailServices svc, string userId, string accountId, string draftId, CancellationToken ct)
    {
        using var doc = await svc.Api(userId, accountId, $"users/me/drafts/{Uri.EscapeDataString(draftId)}?format=full", ct: ct);
        var message = doc.RootElement.GetProperty("message");
        var m = GmailMessageParser.Parse(message);
        return new { id = draftId, messageId = m.Id, threadId = m.ThreadId, to = m.Header("To"), cc = m.Header("Cc"), subject = m.Header("Subject"), body = m.Text, date = m.Date };
    }

    // ── Pub/Sub push receiver ───────────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> ReceivePushAsync(PubSubEnvelope body, string? token, GmailServices svc, CancellationToken ct)
    {
        var expected = svc.Config["Gmail:PubSub:VerificationToken"];
        if (string.IsNullOrWhiteSpace(expected)) return Results.Problem(statusCode: 503, title: "Gmail push notifications aren't configured.");
        if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return Results.Unauthorized();
        if (body.Message?.Data is not { Length: > 0 } data || string.IsNullOrWhiteSpace(body.Message.MessageId)) return Results.BadRequest();

        string email; string? historyId;
        try
        {
            var json = JsonDocument.Parse(Convert.FromBase64String(data)).RootElement;
            email = json.GetProperty("emailAddress").GetString() ?? "";
            historyId = json.TryGetProperty("historyId", out var h) ? (h.ValueKind == JsonValueKind.Number ? h.GetInt64().ToString() : h.GetString()) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return Results.BadRequest(); }
        if (email.Length == 0) return Results.BadRequest();

        // Exactly-once per Pub/Sub message: a duplicate delivery is acknowledged and ignored.
        try
        {
            await svc.Db.GetCollection<GmailWebhookEvent>("gmailWebhookEvents").InsertOneAsync(new GmailWebhookEvent
            { MessageId = body.Message.MessageId, EmailAddress = email.ToLowerInvariant(), HistoryId = historyId, ReceivedUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(2) }, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return Results.NoContent(); }

        var accounts = await svc.Db.GetCollection<GmailAccountDoc>("gmailAccounts").Find(a => a.Email == email.ToLowerInvariant() || a.Email == email).ToListAsync(ct);
        foreach (var a in accounts.Where(a => a.Enabled))
            await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").UpdateOneAsync(s => s.AccountId == a.ConnectionId,
                Builders<GmailSyncState>.Update.SetOnInsert(s => s.UserId, a.UserId).Set(s => s.SyncRequested, true).Set(s => s.LastPushUtc, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);
        return Results.NoContent(); // 2xx acknowledges the message even for unknown accounts so Pub/Sub doesn't redeliver forever
    }

    public static async Task EnsureGmailManagementIndexesAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        async Task Idx<T>(string collection, params CreateIndexModel<T>[] models) => await db.GetCollection<T>(collection).Indexes.CreateManyAsync(models, ct);
        await Idx("gmailAccounts", new CreateIndexModel<GmailAccountDoc>(Builders<GmailAccountDoc>.IndexKeys.Ascending(a => a.UserId)));
        await Idx("gmailMessages",
            new CreateIndexModel<GmailMessageDoc>(Builders<GmailMessageDoc>.IndexKeys.Ascending(d => d.UserId).Ascending(d => d.AccountId).Descending(d => d.DateUtc)),
            new CreateIndexModel<GmailMessageDoc>(Builders<GmailMessageDoc>.IndexKeys.Ascending(d => d.AccountId).Ascending(d => d.ThreadId)),
            new CreateIndexModel<GmailMessageDoc>(Builders<GmailMessageDoc>.IndexKeys.Ascending(d => d.AccountId).Ascending(d => d.ContentHash)),
            new CreateIndexModel<GmailMessageDoc>(Builders<GmailMessageDoc>.IndexKeys.Ascending(d => d.AccountId).Ascending(d => d.NeedsReply).Ascending(d => d.ReplyEvaluated)));
        await Idx("gmailRules", new CreateIndexModel<GmailRule>(Builders<GmailRule>.IndexKeys.Ascending(r => r.UserId).Ascending(r => r.AccountId).Ascending(r => r.Priority)));
        await Idx("gmailRuleRuns",
            new CreateIndexModel<GmailRuleRun>(Builders<GmailRuleRun>.IndexKeys.Ascending(r => r.RuleId).Descending(r => r.AtUtc)),
            new CreateIndexModel<GmailRuleRun>(Builders<GmailRuleRun>.IndexKeys.Ascending(r => r.RuleId).Ascending(r => r.MessageId).Ascending(r => r.Trigger)));
        await Idx("gmailFollowUps", new CreateIndexModel<GmailFollowUp>(Builders<GmailFollowUp>.IndexKeys.Ascending(f => f.UserId).Ascending(f => f.AccountId).Ascending(f => f.Status).Ascending(f => f.DueAtUtc)));
        await Idx("gmailNotifications", new CreateIndexModel<GmailNotification>(Builders<GmailNotification>.IndexKeys.Ascending(n => n.UserId).Ascending(n => n.AccountId).Descending(n => n.CreatedAtUtc)));
        await Idx("gmailActionAudit",
            new CreateIndexModel<GmailActionAudit>(Builders<GmailActionAudit>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.AccountId).Descending(a => a.AtUtc)),
            new CreateIndexModel<GmailActionAudit>(Builders<GmailActionAudit>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.Action)));
        await Idx("gmailSyncHistory", new CreateIndexModel<GmailSyncRun>(Builders<GmailSyncRun>.IndexKeys.Ascending(r => r.AccountId).Descending(r => r.StartedUtc)));
        await Idx("gmailWebhookEvents", new CreateIndexModel<GmailWebhookEvent>(Builders<GmailWebhookEvent>.IndexKeys.Ascending(e => e.ExpiresAtUtc), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_gmailWebhookEvents" }));
        await Idx("gmailAutomationEvents", new CreateIndexModel<GmailAutomationEvent>(Builders<GmailAutomationEvent>.IndexKeys.Ascending(e => e.Consumed).Ascending(e => e.CreatedAtUtc)));
        await Idx("gmailCorrections", new CreateIndexModel<GmailCorrection>(Builders<GmailCorrection>.IndexKeys.Ascending(c => c.UserId).Ascending(c => c.AccountId).Ascending(c => c.Sender)));
        await Idx("gmailAttachments", new CreateIndexModel<GmailAttachmentDoc>(Builders<GmailAttachmentDoc>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.AccountId).Ascending(a => a.Sha256)));
        await Idx("gmailSavedSearches", new CreateIndexModel<GmailSavedSearch>(Builders<GmailSavedSearch>.IndexKeys.Ascending(s => s.UserId)));
    }
}
