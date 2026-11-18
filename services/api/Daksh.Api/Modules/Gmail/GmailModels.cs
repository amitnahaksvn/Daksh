using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Platform.Security;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>Synced message metadata + AI results. Full bodies are never stored; snippets/subject only when the account's privacy setting allows.</summary>
[BsonIgnoreExtraElements]
public sealed class GmailMessageDoc
{
    /// <summary>"{accountId}:{messageId}" — the unique key that makes sync idempotent.</summary>
    [BsonId] public required string Key { get; set; }
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string MessageId { get; set; }
    public string ThreadId { get; set; } = "";
    public List<string> Labels { get; set; } = [];
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Snippet { get; set; } = "";
    public DateTime? DateUtc { get; set; }
    public string? HistoryId { get; set; }
    public bool HasAttachment { get; set; }
    public bool Processed { get; set; }
    public List<string> Categories { get; set; } = [];
    public Dictionary<string, double> Scores { get; set; } = [];
    public bool Important { get; set; }
    public bool Urgent { get; set; }
    public bool Suspicious { get; set; }
    public bool NeedsReply { get; set; }
    public string Sentiment { get; set; } = "neutral";
    public string Summary { get; set; } = "";
    public List<string> ActionItems { get; set; } = [];
    public List<DateTime> Deadlines { get; set; } = [];
    public string ContentHash { get; set; } = "";
    public bool IsDuplicate { get; set; }
    public bool ReplyEvaluated { get; set; }
    public string? CorrectedBy { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailSyncState
{
    /// <summary>The account (connection) id.</summary>
    [BsonId] public required string AccountId { get; set; }
    public required string UserId { get; set; }
    public string Status { get; set; } = "Idle"; // Idle | Synchronising | Failed
    public string? HistoryId { get; set; }
    public bool InitialSyncDone { get; set; }
    public bool SyncRequested { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public string? LastError { get; set; }
    public long MessagesSynced { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? WatchHistoryId { get; set; }
    public DateTime? WatchExpiresUtc { get; set; }
    public DateTime? WatchRenewedUtc { get; set; }
    public DateTime? LastPushUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailSyncRun
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public string Kind { get; set; } = "incremental"; // initial | incremental | full-resync | reconcile
    public string Trigger { get; set; } = "worker";   // worker | manual | push
    public int Fetched { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "ok"; // ok | partial | failed
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    [BsonIgnore] public int ProcessedBox;
    [BsonIgnore] public int FailedBox;
}

[BsonIgnoreExtraElements]
public sealed class GmailFollowUp
{
    [BsonId] public required string Key { get; set; } // "{accountId}:{threadId}"
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string ThreadId { get; set; }
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string Status { get; set; } = GmailFollowUpPlanner.AwaitingMyResponse;
    public DateTime? DueAtUtc { get; set; }
    public DateTime? SnoozedUntilUtc { get; set; }
    public int FollowUpsSent { get; set; }
    public string? DraftId { get; set; }
    public DateTime? LastNotifiedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailNotification
{
    /// <summary>Dedupe key: one notification per (account, type, subject-of-event).</summary>
    [BsonId] public required string Key { get; set; }
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string Type { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? MessageId { get; set; }
    public string? ThreadId { get; set; }
    public string Priority { get; set; } = "normal";
    public bool Read { get; set; }
    /// <summary>Recorded but not delivered (quiet hours / per-type switch off). Still visible in the digest.</summary>
    public bool Suppressed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailActionAudit
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public string AccountId { get; set; } = "";
    public string? MessageRef { get; set; }
    public required string Action { get; set; }
    /// <summary>User | Rule | Automation | Sync | System</summary>
    public string Trigger { get; set; } = "User";
    public string? RuleId { get; set; }
    /// <summary>NotRequired | Approved | Pending | Denied</summary>
    public string Approval { get; set; } = "NotRequired";
    /// <summary>Success | Failed | Denied</summary>
    public string Status { get; set; } = "Success";
    public string? Error { get; set; }
    public DateTime AtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailSavedSearch
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public GmailSearchCriteria Criteria { get; set; } = new();
    public string Query { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailWebhookEvent
{
    /// <summary>Pub/Sub messageId — the unique key that makes duplicate deliveries harmless.</summary>
    [BsonId] public required string MessageId { get; set; }
    public string UserId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public string? HistoryId { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Outbox of Gmail events (new mail, invoice detected, no reply…) for Daksha's central automation engine to consume.</summary>
[BsonIgnoreExtraElements]
public sealed class GmailAutomationEvent
{
    /// <summary>"{accountId}:{trigger}:{subject}" — a given event is emitted once.</summary>
    [BsonId] public required string Key { get; set; }
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string Trigger { get; set; }
    public string? MessageId { get; set; }
    public string? ThreadId { get; set; }
    public Dictionary<string, string> Data { get; set; } = [];
    public bool Consumed { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailDeadLetter
{
    [BsonId] public required string Key { get; set; } // "{accountId}:{messageId}"
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string MessageId { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public bool Dead { get; set; }
    public DateTime LastAttemptUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailReplyLogEntry
{
    [BsonId] public required string Key { get; set; } // "{accountId}:{threadId}" – at most one automatic reply per thread
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public string ThreadId { get; set; } = "";
    public string Outcome { get; set; } = ""; // Draft | Sent | Suggested
    public DateTime AtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailAttachmentDoc
{
    [BsonId] public required string Key { get; set; } // "{accountId}:{messageId}:{attachmentId}"
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string MessageId { get; set; }
    public string FileName { get; set; } = "";
    public string DetectedType { get; set; } = "";
    public string DocumentType { get; set; } = "other";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public bool Blocked { get; set; }
    public string? BlockedReason { get; set; }
    public bool IsDuplicate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record GmailFetchedAttachment(string AttachmentId, string FileName, string MimeType, long Size);

public sealed record GmailFetched(
    string Id, string ThreadId, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, string> Headers, string Text, string? Html,
    IReadOnlyList<GmailFetchedAttachment> Attachments, DateTimeOffset Date, string? HistoryId, string Snippet)
{
    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
}

/// <summary>Turns a Gmail API <c>format=full</c> message into plain data. Pure and unit-tested against recorded payload shapes.</summary>
public static class GmailMessageParser
{
    public static GmailFetched Parse(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? text = null, html = null;
        var attachments = new List<GmailFetchedAttachment>();
        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("headers", out var hs))
                foreach (var h in hs.EnumerateArray())
                    if (h.TryGetProperty("name", out var n) && h.TryGetProperty("value", out var v) && n.GetString() is { } name && !headers.ContainsKey(name))
                        headers[name] = v.GetString() ?? "";
            Walk(payload, ref text, ref html, attachments, 0);
        }
        var labels = root.TryGetProperty("labelIds", out var l) ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        var date = root.TryGetProperty("internalDate", out var d) && long.TryParse(d.GetString(), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.TryParse(headers.GetValueOrDefault("Date"), out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var plain = text ?? (html is null ? "" : StripHtml(html));
        return new GmailFetched(
            root.GetProperty("id").GetString() ?? "", root.TryGetProperty("threadId", out var t) ? t.GetString() ?? "" : "", labels, headers, plain, html,
            attachments, date, root.TryGetProperty("historyId", out var hid) ? hid.GetString() : null, root.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "");
    }

    private static void Walk(JsonElement part, ref string? text, ref string? html, List<GmailFetchedAttachment> attachments, int depth)
    {
        if (depth > 12) return; // a hostile, deeply nested message cannot blow the stack
        var mime = part.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
        var filename = part.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
        if (part.TryGetProperty("body", out var body))
        {
            if (filename.Length > 0 && body.TryGetProperty("attachmentId", out var aid) && aid.GetString() is { Length: > 0 } id)
                attachments.Add(new GmailFetchedAttachment(id, filename, mime, body.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
            else if (body.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } encoded)
            {
                var decoded = Decode(encoded);
                if (mime == "text/plain" && text is null) text = decoded;
                else if (mime == "text/html" && html is null) html = decoded;
            }
        }
        if (part.TryGetProperty("parts", out var parts))
            foreach (var child in parts.EnumerateArray()) Walk(child, ref text, ref html, attachments, depth + 1);
    }

    private static string Decode(string value)
    {
        try
        {
            var n = value.Replace('-', '+').Replace('_', '/');
            return Encoding.UTF8.GetString(Convert.FromBase64String(n.PadRight(n.Length + (4 - n.Length % 4) % 4, '=')));
        }
        catch (FormatException) { return ""; }
    }

    public static string StripHtml(string html)
    {
        var noScripts = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = Regex.Replace(noScripts, @"<br\s*/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"[ \t]+", " ")).Trim();
    }

    public static GmailMessageInput ToInput(GmailFetched m) => new(
        m.Header("From"), m.Header("To"), m.Header("Subject"), m.Text, m.Date, m.Labels,
        ListUnsubscribe: m.Headers.ContainsKey("List-Unsubscribe"),
        BulkPrecedence: m.Header("Precedence").Equals("bulk", StringComparison.OrdinalIgnoreCase) || m.Header("Precedence").Equals("list", StringComparison.OrdinalIgnoreCase),
        AutoSubmitted: m.Header("Auto-Submitted").Length > 0 && !m.Header("Auto-Submitted").Equals("no", StringComparison.OrdinalIgnoreCase),
        HasAttachment: m.Attachments.Count > 0);

    public static GmailMessageFacts ToFacts(GmailFetched m, GmailClassificationResult? cls) => new(
        m.Header("From"), m.Header("To"), m.Header("Cc"), m.Header("Subject"), m.Text, m.Attachments.Count > 0,
        m.Attachments.Select(a => Path.GetExtension(a.FileName)).ToList(), m.Labels,
        m.Labels.Contains("IMPORTANT") || cls?.Important == true, m.Labels.Contains("STARRED"),
        cls?.Scores.Keys.ToList() ?? [], cls?.Urgent == true, m.Date);
}
