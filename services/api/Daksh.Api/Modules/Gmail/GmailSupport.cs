using System.Text;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Attachments: validate the real file type from its bytes, never trust the extension, never execute.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public sealed record GmailAttachmentCheck(string SafeFileName, string Extension, string DetectedType, bool ExtensionMismatch, bool Blocked, string? Reason);

public static class GmailAttachmentSafety
{
    /// <summary>Extensions Daksha refuses to hand out or store: they are directly executable or auto-run in common clients.</summary>
    public static readonly HashSet<string> Unsafe = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "bat", "cmd", "com", "scr", "msi", "msp", "pif", "cpl", "jar", "js", "jse", "vbs", "vbe", "wsf", "wsh", "ps1", "psm1",
        "sh", "bash", "app", "apk", "ipa", "lnk", "hta", "reg", "iso", "dmg", "pkg", "deb", "rpm", "appx", "gadget", "inf", "scf", "url",
    };

    private static readonly (string Type, byte[] Magic, string[] Extensions)[] Signatures =
    [
        ("pdf", "%PDF"u8.ToArray(), ["pdf"]),
        ("png", [0x89, 0x50, 0x4E, 0x47], ["png"]),
        ("jpeg", [0xFF, 0xD8, 0xFF], ["jpg", "jpeg"]),
        ("gif", "GIF8"u8.ToArray(), ["gif"]),
        ("webp", "RIFF"u8.ToArray(), ["webp", "wav", "avi"]),
        ("ole", [0xD0, 0xCF, 0x11, 0xE0], ["doc", "xls", "ppt", "msg"]),
        ("zip", [0x50, 0x4B, 0x03, 0x04], ["zip", "docx", "xlsx", "pptx", "odt", "ods", "epub", "apk", "jar"]),
        ("rtf", "{\\rtf"u8.ToArray(), ["rtf"]),
        ("exe", "MZ"u8.ToArray(), ["exe", "dll", "scr", "com"]),
        ("elf", [0x7F, 0x45, 0x4C, 0x46], []),
        ("macho", [0xCF, 0xFA, 0xED, 0xFE], []),
        ("script", "#!"u8.ToArray(), []),
        ("gzip", [0x1F, 0x8B], ["gz", "tgz"]),
        ("7z", [0x37, 0x7A, 0xBC, 0xAF], ["7z"]),
        ("rar", "Rar!"u8.ToArray(), ["rar"]),
    ];

    private static readonly string[] TextExtensions = ["txt", "csv", "tsv", "md", "json", "xml", "ics", "html", "htm", "log", "eml", "vcf"];

    /// <summary>Strips directories and control characters so a name like <c>../../etc/passwd</c> can never escape a storage folder.</summary>
    public static string SafeFileName(string? name)
    {
        var leaf = (name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var cleaned = new string(leaf.Where(c => !char.IsControl(c) && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|').ToArray()).Trim().Trim('.');
        if (cleaned.Length == 0) cleaned = "attachment";
        return cleaned.Length > 120 ? cleaned[^120..] : cleaned;
    }

    public static GmailAttachmentCheck Inspect(string? fileName, ReadOnlySpan<byte> head, AttachmentConfig? config = null, long? sizeBytes = null)
    {
        var safe = SafeFileName(fileName);
        var ext = Path.GetExtension(safe).TrimStart('.').ToLowerInvariant();
        string detected = "unknown";
        foreach (var (type, magic, _) in Signatures)
            if (head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic)) { detected = type; break; }
        if (detected == "unknown" && head.Length > 0 && IsMostlyText(head)) detected = "text";

        var executable = detected is "exe" or "elf" or "macho" or "script";
        var mismatch = false;
        if (detected is not ("unknown" or "text"))
        {
            var sig = Signatures.First(s => s.Type == detected);
            mismatch = sig.Extensions.Length > 0 && ext.Length > 0 && !sig.Extensions.Contains(ext) && !(detected == "zip" && ext is "docx" or "xlsx" or "pptx");
            if (detected == "webp") mismatch = ext.Length > 0 && !sig.Extensions.Contains(ext);
        }
        else if (detected == "text" && ext.Length > 0 && !TextExtensions.Contains(ext) && ext is "pdf" or "png" or "jpg" or "jpeg" or "gif" or "docx" or "xlsx" or "zip")
            mismatch = true;

        string? reason = null;
        if (executable) reason = "The file is an executable program.";
        else if (Unsafe.Contains(ext)) reason = $"\".{ext}\" files can run code and are blocked.";
        else if (mismatch) reason = $"The file's contents ({detected}) don't match its \".{ext}\" extension.";
        else if (config is not null)
        {
            if (config.ExcludedExtensions.Contains(ext)) reason = $"\".{ext}\" attachments are excluded in your settings.";
            else if (config.AllowedExtensions.Count > 0 && !config.AllowedExtensions.Contains(ext)) reason = $"\".{ext}\" isn't in your allowed attachment types.";
            else if (sizeBytes is { } size && size > config.MaxSizeMb * 1024L * 1024L) reason = $"The file is larger than your {config.MaxSizeMb} MB limit.";
        }
        return new GmailAttachmentCheck(safe, ext, detected, mismatch, reason is not null, reason);
    }

    private static bool IsMostlyText(ReadOnlySpan<byte> head)
    {
        var sample = head[..Math.Min(head.Length, 512)];
        var control = 0;
        foreach (var b in sample) if (b == 0 || (b < 9) || (b is > 13 and < 32)) control++;
        return control == 0;
    }

    /// <summary>Best-effort document type from the file name and the first bit of text. Used to file invoices, resumes, etc.</summary>
    public static string ClassifyDocument(string fileName, string? text = null)
    {
        var hay = (fileName + " " + (text ?? "")).ToLowerInvariant();
        bool Has(params string[] words) => words.Any(hay.Contains);
        if (Has("invoice", "tax invoice", "amount due")) return "invoice";
        if (Has("receipt", "payment received")) return "receipt";
        if (Has("resume", "curriculum vitae", " cv", "cv.", "work experience")) return "resume";
        if (Has("contract", "agreement", "nda", "terms of")) return "contract";
        if (Has("bank statement", "account statement", "statement of account", "closing balance")) return "bankStatement";
        if (Has("purchase order", "po number", "po-", "p.o.")) return "purchaseOrder";
        return "other";
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Auto-reply policy. Decides what Daksha is allowed to do; sending is the last, most restricted outcome.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public enum GmailReplyAction { Skip, Suggest, Draft, Send }

public sealed record GmailReplyDecision(GmailReplyAction Action, string Reason);

public sealed record GmailReplyContext(
    string From, IReadOnlyList<string> Labels, bool IsNewThread, bool AlreadyRepliedInThread, int RepliesLastHour, int RepliesLastDay,
    DateTimeOffset Now, bool MatchedAutoRule, string Body);

public static class GmailReplyPolicy
{
    private static readonly string[] FinancialCats = ["invoice", "receipt", "paymentReminder", "subscription"];

    public static GmailReplyDecision Decide(
        ReplyConfig cfg, string profile, IReadOnlyDictionary<string, bool>? overrides,
        GmailClassificationResult cls, GmailReplyContext ctx, double confidence)
    {
        if (!cfg.SuggestionsEnabled) return new(GmailReplyAction.Skip, "Reply suggestions are turned off.");
        if (cls.Suspicious && cfg.ExcludeSuspicious) return new(GmailReplyAction.Skip, "The message looks suspicious.");
        if (cls.Automated && cfg.ExcludeAutomated) return new(GmailReplyAction.Skip, "Automated or bulk mail.");
        if (cls.Otp && cfg.ExcludeOtpSecurity) return new(GmailReplyAction.Skip, "Security or one-time-code email.");
        if (cfg.ExcludeFinancial && FinancialCats.Any(cls.Scores.ContainsKey)) return new(GmailReplyAction.Skip, "Financial correspondence is excluded.");
        if (cfg.ExcludeLegal && cls.Scores.ContainsKey("legal")) return new(GmailReplyAction.Skip, "Legal correspondence is excluded.");

        var sender = GmailIntelligence.ExtractAddress(ctx.From);
        var domain = sender.Contains('@') ? sender[(sender.IndexOf('@') + 1)..] : "";
        if (cfg.ExcludedSenders.Contains(sender, StringComparer.OrdinalIgnoreCase)) return new(GmailReplyAction.Skip, "Sender is excluded.");
        if (cfg.ExcludedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return new(GmailReplyAction.Skip, "Sender's domain is excluded.");
        if (cfg.ExcludedLabels.Any(l => ctx.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))) return new(GmailReplyAction.Skip, "The message has an excluded label.");
        if (cfg.OnlySenders.Count > 0 && !cfg.OnlySenders.Contains(sender, StringComparer.OrdinalIgnoreCase)) return new(GmailReplyAction.Skip, "Sender isn't on your reply list.");
        if (ctx.IsNewThread && !cfg.ReplyToNewMessages) return new(GmailReplyAction.Skip, "Replying to new messages is off.");
        if (!ctx.IsNewThread && !cfg.ReplyToExistingThreads) return new(GmailReplyAction.Skip, "Replying in existing conversations is off.");
        if (ctx.AlreadyRepliedInThread) return new(GmailReplyAction.Skip, "Already replied in this conversation.");
        if (!cls.NeedsReply) return new(GmailReplyAction.Skip, "The message doesn't appear to need a reply.");

        // From here on Daksha may *suggest*; anything stronger needs the checks below.
        if (confidence < cfg.MinConfidence) return new(GmailReplyAction.Suggest, "Confidence is below your minimum, so Daksha only suggests.");
        if (OutsideBusinessHours(cfg, ctx.Now)) return new(GmailReplyAction.Suggest, "Outside your business hours.");
        if (ctx.RepliesLastHour >= cfg.MaxPerHour || ctx.RepliesLastDay >= cfg.MaxPerDay) return new(GmailReplyAction.Suggest, "Automatic reply limit reached.");

        switch (cfg.Mode)
        {
            case "AskFirst": return new(GmailReplyAction.Suggest, "Approval is required before anything is created.");
            case "DraftOnly": return new(GmailReplyAction.Draft, "Drafting only; never sent automatically.");
        }

        var canSend = cfg.AutoSend && profile == GmailProfiles.Autonomous
                      && GmailPermissionPolicy.IsAllowed(profile, overrides, GmailPermission.Send)
                      && GmailPermissionPolicy.IsAllowed(profile, overrides, GmailPermission.AiAutomations);
        if (!canSend) return new(GmailReplyAction.Draft, "Automatic sending isn't enabled for this account; created a draft instead.");

        var approved = cfg.Mode switch
        {
            "AutoRules" => ctx.MatchedAutoRule,
            "AutoCategories" => cfg.AutoSendCategories.Any(cls.Scores.ContainsKey),
            _ => false,
        };
        if (!approved) return new(GmailReplyAction.Draft, "No approved rule or category matches; created a draft instead.");

        var commitment = CommitmentRisk(cfg, cls, ctx.Body);
        if (commitment is not null) return new(GmailReplyAction.Draft, $"Replying would involve a commitment about {commitment}; created a draft for you to review.");
        return new(GmailReplyAction.Send, "Matched an approved rule or category.");
    }

    /// <summary>The first topic from "never commit to" that this message is asking Daksha to commit to, or null.</summary>
    public static string? CommitmentRisk(ReplyConfig cfg, GmailClassificationResult cls, string body)
    {
        var lower = body.ToLowerInvariant();
        foreach (var topic in cfg.NeverCommitTo)
        {
            var t = topic.ToLowerInvariant();
            var risky = t switch
            {
                var x when x.Contains("meeting") || x.Contains("call") => cls.Scores.ContainsKey("meetingRequest"),
                var x when x.Contains("deadline") => cls.Deadlines.Count > 0,
                var x when x.Contains("payment") => cls.Scores.ContainsKey("paymentReminder") || cls.Scores.ContainsKey("invoice") || lower.Contains("payment"),
                var x when x.Contains("price") || x.Contains("pricing") || x.Contains("negotiat") => lower.Contains("price") || lower.Contains("quote") || lower.Contains("discount") || lower.Contains("pricing"),
                _ => lower.Contains(t),
            };
            if (risky) return topic;
        }
        return null;
    }

    public static bool OutsideBusinessHours(ReplyConfig cfg, DateTimeOffset now)
    {
        if (cfg.Holidays.Contains(now.ToString("yyyy-MM-dd"))) return true;
        if (!cfg.ReplyOnWeekends && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        if (!cfg.OnlyInBusinessHours) return false;
        var start = TimeOnly.Parse(cfg.BusinessHoursStart); var end = TimeOnly.Parse(cfg.BusinessHoursEnd);
        var t = TimeOnly.FromDateTime(now.DateTime);
        return start <= end ? t < start || t >= end : t < start && t >= end;
    }
}

/// <summary>Template-based reply text built from the user's own style settings. No model is involved and nothing is sent anywhere.</summary>
public static class GmailReplyComposer
{
    public static string Compose(ReplyConfig cfg, string recipientName, string subject, string? summary = null)
    {
        var name = string.IsNullOrWhiteSpace(recipientName) ? "" : $" {recipientName.Trim()}";
        var greeting = cfg.Style is "Casual" or "Friendly" && cfg.Greeting == "Hello" ? "Hi" : cfg.Greeting;
        var intro = cfg.Style switch
        {
            "Formal" => $"Thank you for your message regarding \"{subject}\". I acknowledge receipt and will respond in due course.",
            "Friendly" => $"Thanks so much for your message about \"{subject}\" — I've got it and will get back to you soon!",
            "Casual" => $"Thanks for the note about \"{subject}\". Got it — I'll reply properly soon.",
            "Short" => "Thanks — received. I'll get back to you shortly.",
            "Detailed" => $"Thank you for getting in touch about \"{subject}\". I've read your message{(string.IsNullOrWhiteSpace(summary) ? "" : $" (in summary: {summary})")} and will follow up with a full response as soon as I can.",
            _ => $"Thank you for your message regarding \"{subject}\". I've received it and will get back to you shortly.",
        };
        var sb = new StringBuilder();
        sb.Append(greeting).Append(name).Append(",\n\n").Append(intro).Append("\n\n").Append(cfg.Closing);
        if (!string.IsNullOrWhiteSpace(cfg.Signature)) sb.Append(",\n").Append(cfg.Signature.Trim());
        var text = sb.ToString();
        foreach (var phrase in cfg.PhrasesToAvoid.Where(p => p.Length > 0))
            text = Regex.Replace(text, Regex.Escape(phrase), "", RegexOptions.IgnoreCase);
        return text.Length > cfg.MaxLength ? text[..cfg.MaxLength].TrimEnd() + "…" : text;
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Follow-up scheduling (pure)
// ─────────────────────────────────────────────────────────────────────────────────────────────
public static class GmailFollowUpPlanner
{
    public const string AwaitingMyResponse = "AwaitingMyResponse", AwaitingTheirResponse = "AwaitingTheirResponse", FollowUpDue = "FollowUpDue",
        FollowUpDrafted = "FollowUpDrafted", FollowUpSent = "FollowUpSent", Completed = "Completed", Ignored = "Ignored";

    public static readonly string[] Statuses = [AwaitingMyResponse, AwaitingTheirResponse, FollowUpDue, FollowUpDrafted, FollowUpSent, Completed, Ignored];

    /// <summary>When a follow-up is due: the first configured interval counted from the last outgoing message.</summary>
    public static DateTimeOffset DueAt(DateTimeOffset lastOutgoing, IReadOnlyList<int> intervalsDays, int followUpsAlreadySent) =>
        lastOutgoing.AddDays(intervalsDays[Math.Min(followUpsAlreadySent, intervalsDays.Count - 1)]);
}
