using System.Net;
using System.Text;
using System.Text.Json;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MimeKit;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public static partial class GmailIntegrationModule
{
    /// <summary>The account to act on: the explicit one, else the default enabled account, else the most recently connected.</summary>
    internal static async Task<IntegrationConnection?> ResolveConnectionAsync(IMongoDatabase db, string userId, string? connectionId, CancellationToken ct)
    {
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        if (!string.IsNullOrWhiteSpace(connectionId))
            return await connections.Find(c => c.Id == connectionId && c.UserId == userId && c.IntegrationKey == IntegrationKey && c.Status == ConnectionStatus.Connected).FirstOrDefaultAsync(ct);

        var all = await connections.Find(c => c.UserId == userId && c.IntegrationKey == IntegrationKey && c.Status == ConnectionStatus.Connected)
            .SortByDescending(c => c.CreatedAt).ToListAsync(ct);
        if (all.Count <= 1) return all.FirstOrDefault();
        var accounts = await db.GetCollection<GmailAccountDoc>("gmailAccounts").Find(a => a.UserId == userId).ToListAsync(ct);
        var preferred = accounts.Where(a => a.Enabled && a.IsDefault).Select(a => a.ConnectionId).FirstOrDefault();
        return all.FirstOrDefault(c => c.Id == preferred) ?? all.FirstOrDefault(c => accounts.All(a => a.ConnectionId != c.Id || a.Enabled)) ?? all[0];
    }

    internal static async Task<GmailAccountDoc> LoadAccountAsync(IMongoDatabase db, IntegrationConnection connection, CancellationToken ct)
    {
        var accounts = db.GetCollection<GmailAccountDoc>("gmailAccounts");
        var doc = await accounts.Find(a => a.ConnectionId == connection.Id).FirstOrDefaultAsync(ct);
        if (doc is not null) return doc;
        // Connected before multi-account support: create the record lazily.
        await RegisterAccountAsync(db, connection, null, ct);
        return await accounts.Find(a => a.ConnectionId == connection.Id).FirstAsync(ct);
    }

    internal static async Task<GmailAccountConfig> LoadConfigAsync(IMongoDatabase db, string userId, string connectionId, CancellationToken ct)
    {
        var config = await db.GetCollection<GmailAccountConfig>("gmailAccountConfigs").Find(c => c.ConnectionId == connectionId && c.UserId == userId).FirstOrDefaultAsync(ct);
        if (config is not null)
        {
            GmailConfigValidator.Validate(config); // fills any feature/notification keys added after the document was saved
            return config;
        }
        return new GmailAccountConfig { ConnectionId = connectionId, UserId = userId, UpdatedAtUtc = DateTime.UtcNow };
    }

    /// <summary>Creates/updates the per-account record when OAuth completes. The first account is the default; granted scopes replace the old ones.</summary>
    internal static async Task RegisterAccountAsync(IMongoDatabase db, IntegrationConnection connection, string? grantedScopes, CancellationToken ct)
    {
        var accounts = db.GetCollection<GmailAccountDoc>("gmailAccounts");
        var now = DateTime.UtcNow;
        var existing = await accounts.Find(a => a.ConnectionId == connection.Id).FirstOrDefaultAsync(ct);
        var email = connection.AccountIdentifier ?? "";
        if (existing is null)
        {
            var hasOthers = await accounts.Find(a => a.UserId == connection.UserId && a.ConnectionId != connection.Id).AnyAsync(ct);
            var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..] : "";
            await accounts.InsertOneAsync(new GmailAccountDoc
            {
                ConnectionId = connection.Id, UserId = connection.UserId, Email = email, Nickname = "", IsDefault = !hasOthers, Enabled = true,
                GrantedScopes = grantedScopes, IsWorkspace = domain.Length > 0 && !domain.Equals("gmail.com", StringComparison.OrdinalIgnoreCase) && !domain.Equals("googlemail.com", StringComparison.OrdinalIgnoreCase),
                CreatedAtUtc = now, UpdatedAtUtc = now,
            }, cancellationToken: ct);
            return;
        }
        var update = Builders<GmailAccountDoc>.Update.Set(a => a.Email, email).Set(a => a.UpdatedAtUtc, now);
        if (!string.IsNullOrWhiteSpace(grantedScopes)) update = update.Set(a => a.GrantedScopes, grantedScopes);
        await accounts.UpdateOneAsync(a => a.ConnectionId == connection.Id, update, cancellationToken: ct);
    }
}

/// <summary>Everything the Gmail endpoints and the background worker share. Singleton: holds no per-request state.</summary>
public sealed class GmailServices(IMongoDatabase database, ISecretProtector protector, IConfiguration configuration, IHttpClientFactory clients, ILoggerFactory loggerFactory)
{
    private readonly IMongoDatabase db = database;
    private readonly IConfiguration config = configuration;
    public IMongoDatabase Db => db;
    public ILogger Log { get; } = loggerFactory.CreateLogger("Daksh.Api.Gmail");
    public IConfiguration Config => config;

    public Task<JsonDocument> Api(string userId, string? accountId, string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default) =>
        GmailIntegrationModule.GmailApiForAsync(userId, accountId, path, method ?? HttpMethod.Get, body, db, protector, config, clients, ct);

    /// <summary>Raw bytes endpoint (attachments). Uses the same auth/refresh path as <see cref="Api"/>.</summary>
    public async Task<byte[]> AttachmentBytes(string userId, string accountId, string messageId, string attachmentId, CancellationToken ct)
    {
        using var doc = await Api(userId, accountId, $"users/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}", ct: ct);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(data)) throw new GmailIntegrationException(502, "Gmail returned an empty attachment.");
        var n = data.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(n.PadRight(n.Length + (4 - n.Length % 4) % 4, '='));
    }

    public async Task<GmailFetched> Fetch(string userId, string accountId, string messageId, CancellationToken ct)
    {
        using var doc = await Api(userId, accountId, $"users/me/messages/{Uri.EscapeDataString(messageId)}?format=full", ct: ct);
        return GmailMessageParser.Parse(doc.RootElement);
    }

    // ── audit ────────────────────────────────────────────────────────────────────────────────
    public Task Audit(string userId, string accountId, string action, string? messageRef = null, string trigger = "User", string status = "Success",
        string? error = null, string? ruleId = null, string approval = "NotRequired", CancellationToken ct = default) =>
        db.GetCollection<GmailActionAudit>("gmailActionAudit").InsertOneAsync(new GmailActionAudit
        {
            UserId = userId, AccountId = accountId, MessageRef = messageRef, Action = action, Trigger = trigger, RuleId = ruleId,
            Approval = approval, Status = status, Error = error is { Length: > 300 } ? error[..300] : error, AtUtc = DateTime.UtcNow,
        }, cancellationToken: ct);

    // ── notifications (deduplicated, honouring per-type switches, quiet hours and the hourly cap) ──────────
    public async Task Notify(GmailAccountConfig cfg, string type, string dedupeSubject, string title, string body, string? messageId = null, string? threadId = null, CancellationToken ct = default)
    {
        if (!cfg.Notifications.Types.TryGetValue(type, out var on)) return;
        var collection = db.GetCollection<GmailNotification>("gmailNotifications");
        var now = DateTime.UtcNow;
        var suppressed = !on || InQuietHours(cfg.Notifications, now);
        if (!suppressed)
        {
            var sinceHour = now.AddHours(-1);
            var recent = await collection.CountDocumentsAsync(n => n.UserId == cfg.UserId && n.AccountId == cfg.ConnectionId && !n.Suppressed && n.CreatedAtUtc >= sinceHour, cancellationToken: ct);
            suppressed = recent >= cfg.Notifications.MaxPerHour;
        }
        var key = $"{cfg.ConnectionId}:{type}:{dedupeSubject}";
        var priority = type is "urgentEmail" or "connectionExpired" or "oauthPermissionIssue" or "syncFailure" or "sendFailure" ? "high" : type is "newEmail" ? "low" : "normal";
        await collection.UpdateOneAsync(n => n.Key == key,
            Builders<GmailNotification>.Update
                .SetOnInsert(n => n.UserId, cfg.UserId).SetOnInsert(n => n.AccountId, cfg.ConnectionId).SetOnInsert(n => n.Type, type)
                .SetOnInsert(n => n.Title, title).SetOnInsert(n => n.Body, body.Length > 300 ? body[..300] : body)
                .SetOnInsert(n => n.MessageId, messageId).SetOnInsert(n => n.ThreadId, threadId).SetOnInsert(n => n.Priority, priority)
                .SetOnInsert(n => n.Read, false).SetOnInsert(n => n.Suppressed, suppressed).SetOnInsert(n => n.CreatedAtUtc, now),
            new UpdateOptions { IsUpsert = true }, ct);
    }

    public static bool InQuietHours(NotificationConfig cfg, DateTime utcNow)
    {
        if (!TimeOnly.TryParse(cfg.QuietHoursStart, out var start) || !TimeOnly.TryParse(cfg.QuietHoursEnd, out var end)) return false;
        var t = TimeOnly.FromDateTime(utcNow);
        return start <= end ? t >= start && t < end : t >= start || t < end;
    }

    // ── automation events: published once each for the central automation engine ─────────────────────────
    public Task Emit(GmailAccountConfig cfg, string trigger, string subject, string? messageId, string? threadId, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
        db.GetCollection<GmailAutomationEvent>("gmailAutomationEvents").UpdateOneAsync(e => e.Key == $"{cfg.ConnectionId}:{trigger}:{subject}",
            Builders<GmailAutomationEvent>.Update
                .SetOnInsert(e => e.UserId, cfg.UserId).SetOnInsert(e => e.AccountId, cfg.ConnectionId).SetOnInsert(e => e.Trigger, trigger)
                .SetOnInsert(e => e.MessageId, messageId).SetOnInsert(e => e.ThreadId, threadId).SetOnInsert(e => e.Data, data ?? [])
                .SetOnInsert(e => e.Consumed, false).SetOnInsert(e => e.CreatedAtUtc, DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true }, ct);

    // ── message actions (used by endpoints and by automatic rules) ───────────────────────────────────────
    public static readonly IReadOnlyDictionary<string, (string Permission, string[] Add, string[] Remove)> LabelActions = new Dictionary<string, (string, string[], string[])>
    {
        ["archive"] = (GmailPermission.Archive, [], ["INBOX"]),
        ["unarchive"] = (GmailPermission.Archive, ["INBOX"], []),
        ["read"] = (GmailPermission.MarkRead, [], ["UNREAD"]),
        ["unread"] = (GmailPermission.MarkRead, ["UNREAD"], []),
        ["star"] = (GmailPermission.Star, ["STARRED"], []),
        ["unstar"] = (GmailPermission.Star, [], ["STARRED"]),
        ["important"] = (GmailPermission.ApplyLabels, ["IMPORTANT"], []),
        ["unimportant"] = (GmailPermission.ApplyLabels, [], ["IMPORTANT"]),
    };

    public async Task ModifyLabels(string userId, string accountId, string messageId, IEnumerable<string> add, IEnumerable<string> remove, CancellationToken ct)
    {
        using var _ = await Api(userId, accountId, $"users/me/messages/{Uri.EscapeDataString(messageId)}/modify", HttpMethod.Post,
            new { addLabelIds = add.ToArray(), removeLabelIds = remove.ToArray() }, ct);
    }

    public async Task<string> EnsureLabel(string userId, string accountId, string name, CancellationToken ct)
    {
        using (var list = await Api(userId, accountId, "users/me/labels", ct: ct))
            if (list.RootElement.TryGetProperty("labels", out var arr))
                foreach (var l in arr.EnumerateArray())
                    if (string.Equals(l.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) return l.GetProperty("id").GetString()!;
        using var created = await Api(userId, accountId, "users/me/labels", HttpMethod.Post,
            new { name, labelListVisibility = "labelShow", messageListVisibility = "show" }, ct);
        return created.RootElement.GetProperty("id").GetString()!;
    }

    public static string BuildMime(string from, string to, string? cc, string subject, string body, string? inReplyTo = null, string? references = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.AddRange(InternetAddressList.Parse(to));
        if (!string.IsNullOrWhiteSpace(cc)) message.Cc.AddRange(InternetAddressList.Parse(cc));
        message.Subject = subject.Replace("\r", " ").Replace("\n", " ");
        if (!string.IsNullOrWhiteSpace(inReplyTo)) message.InReplyTo = inReplyTo;
        if (!string.IsNullOrWhiteSpace(references))
            foreach (var r in references.Split(' ', StringSplitOptions.RemoveEmptyEntries)) message.References.Add(r);
        message.Body = new TextPart("plain") { Text = body };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(message.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Headers a reply needs so Gmail threads it: <c>In-Reply-To</c> is the original Message-ID; <c>References</c> is the chain plus it.</summary>
    public static (string InReplyTo, string References, string Subject) ReplyHeaders(GmailFetched original)
    {
        var id = original.Header("Message-ID");
        if (string.IsNullOrWhiteSpace(id)) id = original.Header("Message-Id");
        var refs = (original.Header("References") + " " + id).Trim();
        var subject = original.Header("Subject");
        return (id, refs, subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject);
    }

    // ── attachments: record metadata (never bytes) so duplicates and document types are known ────────────
    public async Task<(GmailAttachmentCheck Check, string Sha256, bool Duplicate, string DocumentType)> InspectAttachment(
        string userId, string accountId, GmailAccountConfig cfg, string messageId, GmailFetchedAttachment att, byte[] bytes, CancellationToken ct)
    {
        var check = GmailAttachmentSafety.Inspect(att.FileName, bytes.AsSpan(0, Math.Min(bytes.Length, 512)), cfg.Attachments, bytes.LongLength);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var collection = db.GetCollection<GmailAttachmentDoc>("gmailAttachments");
        var duplicate = cfg.Attachments.DetectDuplicates && await collection.Find(a => a.UserId == userId && a.AccountId == accountId && a.Sha256 == sha && a.MessageId != messageId).AnyAsync(ct);
        var textHint = check.DetectedType == "text" ? Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4000)) : null;
        var docType = GmailAttachmentSafety.ClassifyDocument(att.FileName, textHint);
        await collection.ReplaceOneAsync(a => a.Key == $"{accountId}:{messageId}:{att.AttachmentId}", new GmailAttachmentDoc
        {
            Key = $"{accountId}:{messageId}:{att.AttachmentId}", UserId = userId, AccountId = accountId, MessageId = messageId, FileName = check.SafeFileName,
            DetectedType = check.DetectedType, DocumentType = docType, SizeBytes = bytes.LongLength, Sha256 = sha, Blocked = check.Blocked,
            BlockedReason = check.Reason, IsDuplicate = duplicate, CreatedAtUtc = DateTime.UtcNow,
        }, new ReplaceOptions { IsUpsert = true }, ct);
        return (check, sha, duplicate, docType);
    }
}
