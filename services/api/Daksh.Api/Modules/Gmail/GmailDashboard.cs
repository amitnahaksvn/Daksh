using Daksh.Api.Auth;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public static partial class GmailIntegrationModule
{
    /// <summary>The badge a priority email gets, from what the classifier found. Order matters: the most actionable label wins.</summary>
    public static string PriorityLabel(GmailMessageDoc d) =>
        d.Urgent || d.ActionItems.Count > 0 ? "Action Required"
        : d.Categories.Contains("paymentReminder") || d.Categories.Contains("invoice") ? "Payment"
        : d.Categories.Contains("meetingRequest") ? "Meeting"
        : d.Categories.Contains("recruitment") ? "Interview"
        : "Important";

    private sealed record PriorityRow(string Id, string ThreadId, string From, string Subject, DateTime? Date, string Label, bool Unread, bool Starred, bool Analysed);

    private static void MapDashboard(RouteGroupBuilder g)
    {
        // `since` = the start of the user's local day (unix seconds), so "today" means today where they are.
        g.MapGet("/dashboard", (string? accountId, long? since, GmailServices svc, ClaimsPrincipalAccessor who, CancellationToken ct) => Run(svc, async () =>
        {
            var auth = await GmailGuard.AuthorizeAsync(svc.Db, who.UserId, accountId, GmailPermission.Read, ct);
            var userId = who.UserId; var id = auth.Connection.Id;
            var now = DateTimeOffset.UtcNow;
            var dayStart = since is { } s && s >= now.AddHours(-26).ToUnixTimeSeconds() && s <= now.AddHours(1).ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(s) : new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var startUnix = dayStart.ToUnixTimeSeconds();

            async Task<List<string>> Ids(string q)
            {
                var ids = new List<string>(); string? page = null;
                do
                {
                    var p = new Dictionary<string, string?> { ["q"] = q, ["maxResults"] = "500" };
                    if (page is not null) p["pageToken"] = page;
                    using var list = await svc.Api(userId, id, QueryHelpers.AddQueryString("users/me/messages", p), ct: ct);
                    if (list.RootElement.TryGetProperty("messages", out var arr)) ids.AddRange(arr.EnumerateArray().Select(m => m.GetProperty("id").GetString()!));
                    page = list.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
                } while (page is not null && ids.Count < 1000);
                return ids;
            }

            var todayIds = await Ids($"in:inbox after:{startUnix}");
            var yesterdayIds = await Ids($"in:inbox after:{startUnix - 86400} before:{startUnix}");
            var importantIds = await Ids($"in:inbox is:important after:{startUnix}");
            int unread;
            using (var label = await svc.Api(userId, id, "users/me/labels/INBOX", ct: ct))
                unread = label.RootElement.TryGetProperty("messagesUnread", out var u) ? u.GetInt32() : 0;

            // AI + follow-up data comes from what Daksha has synced; it is empty until the first sync finishes.
            var docs = svc.Db.GetCollection<GmailMessageDoc>("gmailMessages");
            var state = await svc.Db.GetCollection<GmailSyncState>("gmailSyncState").Find(x => x.AccountId == id).FirstOrDefaultAsync(ct);
            var analysedToday = (int)await docs.CountDocumentsAsync(d => d.UserId == userId && d.AccountId == id && d.Processed && d.DateUtc >= dayStart.UtcDateTime, cancellationToken: ct);
            var aiImportant = await docs.Find(d => d.UserId == userId && d.AccountId == id && d.Processed && (d.Important || d.Urgent) && d.Labels.Contains("INBOX"))
                .SortByDescending(d => d.DateUtc).Limit(25).ToListAsync(ct);
            var importantToday = importantIds.Union(aiImportant.Where(d => d.DateUtc >= dayStart.UtcDateTime).Select(d => d.MessageId)).Distinct().Count();

            var followUps = svc.Db.GetCollection<GmailFollowUp>("gmailFollowUps");
            var needResponse = await followUps.Find(f => f.UserId == userId && f.AccountId == id && f.Status == GmailFollowUpPlanner.AwaitingMyResponse
                && (f.SnoozedUntilUtc == null || f.SnoozedUntilUtc <= now.UtcDateTime)).SortBy(f => f.DueAtUtc).Limit(50).ToListAsync(ct);

            var weekAhead = now.UtcDateTime.Date.AddDays(8);
            var deadlineFilter = new BsonDocument { ["UserId"] = userId, ["AccountId"] = id, ["Deadlines"] = new BsonDocument("$elemMatch", new BsonDocument { ["$gte"] = now.UtcDateTime.Date, ["$lt"] = weekAhead }) };
            var deadlineDocs = await docs.Find(new BsonDocumentFilterDefinition<GmailMessageDoc>(deadlineFilter)).SortByDescending(d => d.DateUtc).Limit(25).ToListAsync(ct);

            // Priority list: classifier-flagged mail first, topped up from Gmail's own "important" marker. Never invented.
            var rows = aiImportant.Take(5).Select(d => new PriorityRow(d.MessageId, d.ThreadId, d.From, d.Subject, d.DateUtc, PriorityLabel(d), d.Labels.Contains("UNREAD"), d.Labels.Contains("STARRED"), true)).ToList();
            if (rows.Count < 5)
            {
                var extra = (await Ids("in:inbox is:important")).Where(x => rows.All(r => r.Id != x)).Take(5 - rows.Count).ToList();
                foreach (var mid in extra)
                {
                    try
                    {
                        using var detail = await svc.Api(userId, id, $"users/me/messages/{Uri.EscapeDataString(mid)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject", ct: ct);
                        var root = detail.RootElement;
                        var headers = root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("headers", out var hs) ? hs.EnumerateArray().ToArray() : [];
                        var labels = root.TryGetProperty("labelIds", out var lv) ? lv.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
                        DateTime? date = root.TryGetProperty("internalDate", out var dv) && long.TryParse(dv.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
                        rows.Add(new PriorityRow(mid, root.TryGetProperty("threadId", out var t) ? t.GetString() ?? "" : "", Header(headers, "From"), Header(headers, "Subject"), date, "Important", labels.Contains("UNREAD"), labels.Contains("STARRED"), false));
                    }
                    catch (GmailIntegrationException ex) when (ex.StatusCode == 404) { }
                }
                rows = rows.OrderByDescending(r => r.Date).ToList();
            }

            var synced = state?.InitialSyncDone == true;
            return Results.Ok(new
            {
                generatedAtUtc = now.UtcDateTime,
                account = new { id, email = auth.Account.Email, auth.Account.Nickname, auth.Account.Enabled },
                counts = new
                {
                    today = todayIds.Count, yesterday = yesterdayIds.Count, capped = todayIds.Count >= 1000 || yesterdayIds.Count >= 1000,
                    unread, important = importantToday,
                    needResponse = synced ? needResponse.Count : (int?)null,
                    deadlines = synced ? deadlineDocs.Count : (int?)null,
                },
                priority = rows.Select(r => new { id = r.Id, threadId = r.ThreadId, from = r.From, subject = r.Subject, date = r.Date, label = r.Label, unread = r.Unread, starred = r.Starred, analysed = r.Analysed }),
                briefing = new
                {
                    needResponse = needResponse.Take(5).Select(f => new { messageId = f.MessageId, threadId = f.ThreadId, subject = f.Subject, from = f.Counterparty, dueAtUtc = f.DueAtUtc }),
                    deadlines = deadlineDocs.Take(5).Select(d => new { messageId = d.MessageId, subject = d.Subject, from = d.From, next = d.Deadlines.Where(x => x >= now.UtcDateTime.Date).OrderBy(x => x).Cast<DateTime?>().FirstOrDefault() }),
                },
                sync = new { status = state?.Status ?? "Idle", state?.LastSuccessUtc, state?.LastFailureUtc, state?.LastError, initialSyncDone = synced, analysedToday },
                aiEnabled = auth.Config.Ai.Enabled,
            });
        }));
    }
}
