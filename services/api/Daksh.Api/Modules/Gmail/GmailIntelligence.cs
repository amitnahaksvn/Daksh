using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

public sealed record GmailMessageInput(
    string From, string To, string Subject, string Body, DateTimeOffset Date,
    IReadOnlyList<string> Labels, bool ListUnsubscribe = false, bool BulkPrecedence = false, bool AutoSubmitted = false, bool HasAttachment = false);

public sealed record GmailDeadline(DateTimeOffset Date, string Context);

public sealed record GmailClassificationResult(
    IReadOnlyDictionary<string, double> Scores,
    bool Important, bool Urgent, bool Suspicious, bool NeedsReply, bool Automated, bool Otp,
    string Sentiment, string Summary,
    IReadOnlyList<string> ActionItems, IReadOnlyList<GmailDeadline> Deadlines,
    IReadOnlyList<string> Decisions, IReadOnlyList<string> Contacts,
    string ContentHash, string? CorrectedBy);

[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements]
public sealed class GmailCorrection
{
    [MongoDB.Bson.Serialization.Attributes.BsonId, MongoDB.Bson.Serialization.Attributes.BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
    public string Id { get; set; } = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    /// <summary>Lower-cased sender address the correction applies to.</summary>
    public required string Sender { get; set; }
    public required string Category { get; set; }
    /// <summary>true = "this sender IS this category"; false = "this sender is NOT this category".</summary>
    public bool Is { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Local, deterministic email understanding. Email content is untrusted data: nothing in a message can change what this
/// does (injection phrases are stripped before scoring and surfaced as <c>Suspicious</c>). No email text leaves the server here.
/// </summary>
public static class GmailIntelligence
{
    public static readonly string[] FeatureIds =
    [
        "summarisation", "longThreadSummary", "important", "urgent", "actionItems", "deadlines", "meetingRequest", "invoice", "receipt",
        "paymentReminder", "recruitment", "travel", "flightHotel", "newsletter", "promotional", "spamSuspicious", "sentiment", "complaint",
        "duplicate", "followUp", "decisions", "contacts", "orderDelivery", "subscription", "legal",
    ];

    private sealed record Detector(string Id, string[] Subject, string[] Body, double Base = 0.55);

    private static readonly Detector[] Detectors =
    [
        new("urgent", ["urgent", "asap", "immediately", "time sensitive", "final notice", "last chance", "action required"], ["urgent", "asap", "immediately", "as soon as possible", "time-sensitive", "by end of day", "eod"]),
        new("important", ["important", "action required", "approval", "please review", "required", "reminder"], ["important", "please review", "your approval", "need your", "kindly confirm", "please confirm"]),
        new("meetingRequest", ["meeting", "invitation", "invite", "schedule", "call", "calendar", "zoom", "google meet", "teams"], ["let's meet", "can we meet", "schedule a call", "meeting invite", "join the meeting", "are you available", "calendar invite"]),
        new("invoice", ["invoice", "bill", "statement due"], ["invoice number", "invoice #", "amount due", "total due", "bill to", "please find attached invoice", "gst", "vat"]),
        new("receipt", ["receipt", "payment received", "payment confirmation", "your payment"], ["receipt number", "thank you for your payment", "payment of", "has been received", "paid on", "transaction id"]),
        new("paymentReminder", ["payment reminder", "overdue", "past due", "payment due", "due soon", "outstanding"], ["payment is overdue", "past due", "please pay", "outstanding balance", "pay by", "due date"]),
        new("recruitment", ["interview", "job", "position", "application", "candidate", "recruit", "hiring", "offer letter", "opportunity"], ["interview", "your application", "the position", "resume", "cv", "hiring manager", "recruiter", "job opening"]),
        new("travel", ["booking", "itinerary", "reservation", "trip", "e-ticket", "boarding pass", "check-in"], ["booking reference", "itinerary", "confirmation number", "pnr", "check-in", "departure", "arrival"]),
        new("flightHotel", ["flight", "hotel", "airline", "airbnb", "booking.com", "check-in", "boarding"], ["flight", "boarding pass", "terminal", "hotel", "room type", "check-out", "airline"]),
        new("newsletter", ["newsletter", "digest", "weekly", "monthly", "roundup", "issue #", "edition"], ["unsubscribe", "view in browser", "view this email in your browser", "manage preferences", "you are receiving this"], 0.5),
        new("promotional", ["sale", "% off", "discount", "offer", "deal", "coupon", "free shipping", "limited time", "save"], ["shop now", "limited time", "use code", "promo code", "buy now", "discount", "% off", "exclusive offer"], 0.5),
        new("spamSuspicious", ["you have won", "winner", "claim your", "verify your account", "account suspended", "lottery", "prince"], ["click here immediately", "verify your password", "wire transfer", "gift card", "bitcoin", "you have been selected", "confirm your identity", "account will be closed"], 0.5),
        new("complaint", ["complaint", "unacceptable", "disappointed", "refund", "escalat", "not working", "issue with"], ["unacceptable", "very disappointed", "i want a refund", "terrible", "worst", "not happy", "poor service", "demand", "legal action"]),
        new("orderDelivery", ["order", "shipped", "delivered", "out for delivery", "tracking", "dispatch", "your package"], ["tracking number", "has shipped", "out for delivery", "delivered", "order number", "estimated delivery", "courier"]),
        new("subscription", ["subscription", "renewal", "renew", "trial", "membership", "auto-renew", "billing cycle"], ["will renew", "renews on", "subscription", "trial ends", "cancel anytime", "billing period", "your plan"]),
        new("legal", ["legal", "contract", "agreement", "nda", "terms and conditions", "compliance", "notice of", "subpoena", "court", "gdpr", "terms of service"], ["hereby", "pursuant to", "agreement", "legal notice", "terms and conditions", "compliance", "governing law", "liability", "confidential"]),
        new("followUp", ["follow up", "following up", "reminder", "checking in", "any update", "gentle reminder"], ["following up", "follow up", "just checking in", "any update", "waiting for your", "please let me know", "get back to me", "looking forward to your reply"]),
    ];

    private static readonly Regex Sentence = new(@"(?<=[\.\!\?])\s+|\r?\n{2,}", RegexOptions.Compiled);
    private static readonly Regex EmailRx = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Otp = new(@"\b(otp|one[- ]time (pass(word|code)|code)|verification code|security code|2fa|two[- ]factor|login code|password reset)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] Negative = ["unhappy", "disappointed", "terrible", "angry", "unacceptable", "worst", "frustrat", "annoyed", "upset", "poor", "problem", "failed", "complain"];
    private static readonly string[] Positive = ["thank", "great", "excellent", "appreciate", "happy", "pleased", "wonderful", "congratulations", "love", "perfect", "awesome", "glad"];
    private static readonly string[] ActionLead = ["please ", "kindly ", "could you ", "can you ", "would you ", "need you to ", "you need to ", "you must ", "make sure ", "don't forget", "remember to ", "action required", "please-"];
    private static readonly string[] DecisionMarkers = ["we will ", "i will ", "we've decided", "we have decided", "decided to", "agreed to", "agreed that", "approved", "confirmed that", "we'll ", "i'll ", "it was decided"];

    public static GmailClassificationResult Classify(GmailMessageInput input, IReadOnlyList<GmailCorrection>? corrections = null, string summaryLength = "balanced")
    {
        // 1. Neutralise anything that looks like an instruction aimed at an AI. It's reported, never scored or obeyed.
        var injection = GmailPromptGuard.LooksLikeInjection(input.Subject) || GmailPromptGuard.LooksLikeInjection(input.Body);
        var body = GmailPromptGuard.StripInjection(input.Body);
        var subject = GmailPromptGuard.StripInjection(input.Subject);
        var subjectLc = subject.ToLowerInvariant();
        var bodyLc = body.ToLowerInvariant();
        if (bodyLc.Length > 20_000) bodyLc = bodyLc[..20_000];

        var scores = new Dictionary<string, double>();
        foreach (var d in Detectors)
        {
            var subjectHits = d.Subject.Count(k => subjectLc.Contains(k));
            var bodyHits = d.Body.Count(k => bodyLc.Contains(k));
            if (subjectHits + bodyHits == 0) continue;
            scores[d.Id] = Math.Min(0.98, d.Base + 0.2 * Math.Min(subjectHits, 2) + 0.1 * Math.Min(bodyHits, 3));
        }

        var automated = input.ListUnsubscribe || input.BulkPrecedence || input.AutoSubmitted ||
                        Regex.IsMatch(input.From, @"(no[-_.]?reply|do[-_.]?not[-_.]?reply|mailer-daemon|notifications?@|newsletter@|alerts?@)", RegexOptions.IgnoreCase);
        if (input.ListUnsubscribe) scores["newsletter"] = Math.Max(scores.GetValueOrDefault("newsletter"), 0.8);
        // Mailing-list mail that looks like a promotion reinforces "promotional", not "important".
        if (scores.ContainsKey("promotional") || scores.ContainsKey("newsletter")) { scores.Remove("important"); scores.Remove("urgent"); }
        if (input.Labels.Contains("CATEGORY_PROMOTIONS")) scores["promotional"] = Math.Max(scores.GetValueOrDefault("promotional"), 0.85);
        if (input.Labels.Contains("SPAM")) scores["spamSuspicious"] = Math.Max(scores.GetValueOrDefault("spamSuspicious"), 0.95);
        if (input.Labels.Contains("IMPORTANT")) scores["important"] = Math.Max(scores.GetValueOrDefault("important"), 0.75);
        var otp = Otp.IsMatch(subject) || Otp.IsMatch(body);
        if (injection) scores["spamSuspicious"] = Math.Max(scores.GetValueOrDefault("spamSuspicious"), 0.7);

        // 2. User corrections for this sender override the heuristic (and are reported as such).
        string? correctedBy = null;
        var senderAddress = ExtractAddress(input.From);
        foreach (var c in corrections ?? [])
        {
            if (!string.Equals(c.Sender, senderAddress, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Is) scores[c.Category] = 1.0; else scores.Remove(c.Category);
            correctedBy = "user";
        }

        var actions = ExtractActionItems(body);
        var deadlines = ExtractDeadlines(body + "\n" + subject, input.Date);
        var summary = Summarise(subject, body, summaryLength);
        var needsReply = !automated && !otp && (scores.ContainsKey("followUp") || scores.ContainsKey("meetingRequest") || body.Contains('?') || actions.Count > 0)
                         && !scores.ContainsKey("newsletter") && !scores.ContainsKey("promotional");

        return new GmailClassificationResult(
            scores,
            Important: scores.GetValueOrDefault("important") >= 0.6 || scores.GetValueOrDefault("urgent") >= 0.6,
            Urgent: scores.GetValueOrDefault("urgent") >= 0.6,
            Suspicious: scores.GetValueOrDefault("spamSuspicious") >= 0.6 || injection,
            NeedsReply: needsReply,
            Automated: automated,
            Otp: otp,
            Sentiment: Sentiment(bodyLc),
            Summary: summary,
            ActionItems: actions,
            Deadlines: deadlines,
            Decisions: ExtractDecisions(body),
            Contacts: EmailRx.Matches(input.From + " " + input.To + " " + body).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(10).ToList(),
            ContentHash: Hash(subject + "\n" + body),
            CorrectedBy: correctedBy);
    }

    /// <summary>Which categories survive the user's per-feature switches and confidence thresholds.</summary>
    public static IReadOnlyList<string> EffectiveCategories(GmailClassificationResult result, AiConfig ai) =>
        result.Scores.Where(kv => ai.Features.TryGetValue(kv.Key, out var f) && f.Enabled && kv.Value >= f.ConfidenceThreshold)
            .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();

    /// <summary>Whether AI processing should run for this message at all under the account's scope and exclusions.</summary>
    public static bool ShouldProcess(AiConfig ai, string from, IReadOnlyList<string> labels, GmailClassificationResult? preliminary = null)
    {
        if (!ai.Enabled) return false;
        var address = ExtractAddress(from);
        var domain = address.Contains('@') ? address[(address.IndexOf('@') + 1)..] : "";
        if (ai.ExcludedSenders.Contains(address, StringComparer.OrdinalIgnoreCase)) return false;
        if (ai.ExcludedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return false;
        if (ai.ExcludedLabels.Any(l => labels.Contains(l, StringComparer.OrdinalIgnoreCase))) return false;
        if (preliminary is not null)
        {
            if (ai.Scope == "important" && !preliminary.Important) return false;
            if (ai.Scope == "categories" && !ai.Categories.Any(preliminary.Scores.ContainsKey)) return false;
        }
        return true;
    }

    public static string ExtractAddress(string from)
    {
        var m = EmailRx.Match(from ?? "");
        return m.Success ? m.Value.ToLowerInvariant() : (from ?? "").Trim().ToLowerInvariant();
    }

    public static string Summarise(string subject, string body, string length)
    {
        var count = length switch { "short" => 1, "detailed" => 5, _ => 2 };
        var sentences = Sentence.Split(Regex.Replace(body, @"\s+", " ").Trim())
            .Select(s => s.Trim()).Where(s => s.Length >= 20 && !s.StartsWith('>') && !Regex.IsMatch(s, @"^(hi|hello|dear|regards|thanks|best)\b", RegexOptions.IgnoreCase)).Take(count).ToList();
        var text = sentences.Count == 0 ? subject : string.Join(" ", sentences);
        var cap = length switch { "short" => 160, "detailed" => 900, _ => 360 };
        return text.Length <= cap ? text : text[..(cap - 1)].TrimEnd() + "…";
    }

    public static IReadOnlyList<string> ExtractActionItems(string body) =>
        Sentence.Split(body).Select(s => s.Trim()).Where(s => s.Length is >= 12 and <= 300)
            .Where(s => ActionLead.Any(a => s.StartsWith(a, StringComparison.OrdinalIgnoreCase) || s.Contains(a, StringComparison.OrdinalIgnoreCase) && s.Length < 160))
            .Select(s => s.Length > 200 ? s[..199] + "…" : s).Distinct().Take(8).ToList();

    public static IReadOnlyList<string> ExtractDecisions(string body) =>
        Sentence.Split(body).Select(s => s.Trim()).Where(s => s.Length is >= 12 and <= 300 && DecisionMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Distinct().Take(6).ToList();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly Regex DeadlineCue = new(@"\b(by|before|due(?: on| by)?|deadline(?: is)?|no later than|until|on or before|expires? on)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IsoDate = new(@"\b(20\d{2})-(\d{2})-(\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex DayMonth = new(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s*(20\d{2})?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MonthDay = new(@"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s*(20\d{2}))?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Weekday = new(@"\b(?:by|before|on|until)\s+(?:this\s+|next\s+)?(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Relative = new(@"\b(?:by|before|until)\s+(tomorrow|tonight|end of (?:the )?(?:day|week|month)|eod|eow)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Dates that follow a deadline cue ("by Friday", "due 12 Dec 2026"). Relative dates resolve against the message date, not "now".</summary>
    public static IReadOnlyList<GmailDeadline> ExtractDeadlines(string text, DateTimeOffset received)
    {
        var results = new List<GmailDeadline>();
        foreach (var raw in Sentence.Split(text))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0 || sentence.Length > 400 || !(DeadlineCue.IsMatch(sentence) || Weekday.IsMatch(sentence))) continue;
            var found = new List<DateTimeOffset>();
            foreach (Match m in IsoDate.Matches(sentence)) TryAdd(found, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            foreach (Match m in DayMonth.Matches(sentence)) TryAdd(found, YearOr(m.Groups[3], received, MonthIndex(m.Groups[2].Value), int.Parse(m.Groups[1].Value)), MonthIndex(m.Groups[2].Value), int.Parse(m.Groups[1].Value));
            foreach (Match m in MonthDay.Matches(sentence)) TryAdd(found, YearOr(m.Groups[3], received, MonthIndex(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), MonthIndex(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            foreach (Match m in Weekday.Matches(sentence))
            {
                var target = Enum.Parse<DayOfWeek>(m.Groups[1].Value, true);
                var delta = ((int)target - (int)received.DayOfWeek + 7) % 7;
                found.Add(new DateTimeOffset(received.UtcDateTime.Date.AddDays(delta == 0 ? 7 : delta), TimeSpan.Zero));
            }
            foreach (Match m in Relative.Matches(sentence))
            {
                var d = received.UtcDateTime.Date;
                var token = m.Groups[1].Value.ToLowerInvariant();
                found.Add(new DateTimeOffset(token.StartsWith("tomorrow") ? d.AddDays(1) : token.Contains("week") || token == "eow" ? d.AddDays(((int)DayOfWeek.Friday - (int)d.DayOfWeek + 7) % 7) : d, TimeSpan.Zero));
            }
            foreach (var date in found)
                if (!results.Any(r => r.Date == date)) results.Add(new GmailDeadline(date, sentence.Length > 160 ? sentence[..159] + "…" : sentence));
        }
        return results.OrderBy(r => r.Date).Take(6).ToList();
    }

    private static int MonthIndex(string name) => Array.IndexOf(Months, name[..3].ToLowerInvariant()) + 1;
    private static int YearOr(Group year, DateTimeOffset received, int month, int day)
    {
        if (year.Success) return int.Parse(year.Value);
        // No year written: take the next occurrence on or after the day the message arrived.
        var candidate = new DateTime(received.Year, month, Math.Min(day, DateTime.DaysInMonth(received.Year, month)));
        return candidate.Date < received.UtcDateTime.Date.AddDays(-1) ? received.Year + 1 : received.Year;
    }
    private static void TryAdd(List<DateTimeOffset> list, int year, int month, int day)
    {
        if (month is < 1 or > 12 || day < 1 || year is < 2000 or > 2100 || day > DateTime.DaysInMonth(year, month)) return;
        list.Add(new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc)));
    }

    private static string Sentiment(string lower)
    {
        var neg = Negative.Count(lower.Contains);
        var pos = Positive.Count(lower.Contains);
        return neg > pos ? "negative" : pos > neg ? "positive" : "neutral";
    }

    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim())));
}

/// <summary>Defences against emails that try to instruct an AI agent.</summary>
public static class GmailPromptGuard
{
    private static readonly Regex[] Patterns =
    [
        new(@"ignore\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions?|prompts?|messages?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"disregard\s+(all\s+|any\s+|the\s+)?(previous|prior|above|your)\s+(instructions?|rules?|guidelines?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\byou\s+are\s+now\b|\bact\s+as\s+(an?\s+)?(admin|system|assistant|ai)\b|\bnew\s+instructions?\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bsystem\s*prompt\b|\bdeveloper\s*(mode|message)\b|\bjailbreak\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(forward|send|email)\s+(this|all|every|the)[^.\n]{0,60}\s+to\s+[\w.+-]+@[\w.-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(delete|erase|trash)\s+(all|every)\s+(emails?|messages?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(reveal|print|share|leak)\s+(your|the)\s+(instructions|prompt|token|password|api\s*key|secret)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?i)\b(mark|classify|treat)\s+(this|it)\s+as\s+(urgent|important|safe|trusted)\b", RegexOptions.Compiled),
        new(@"<\s*/?\s*(system|assistant|untrusted_email)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static bool LooksLikeInjection(string? text) => !string.IsNullOrEmpty(text) && Patterns.Any(p => p.IsMatch(text));

    /// <summary>Removes lines that look like instructions to an AI so they cannot influence classification or extraction.</summary>
    public static string StripInjection(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var kept = text.Split('\n').Where(line => !Patterns.Any(p => p.IsMatch(line)));
        return string.Join('\n', kept);
    }

    /// <summary>
    /// Wraps email text for a future LLM call as clearly-delimited, untrusted data. The delimiter tag is neutralised inside the text so the
    /// email cannot "close" the block. The surrounding system prompt must state that the block is data and never instructions.
    /// </summary>
    public static string WrapUntrusted(string? text, int maxChars = 8000)
    {
        var safe = Regex.Replace(text ?? "", @"<\s*/?\s*untrusted_email[^>]*>", "[removed]", RegexOptions.IgnoreCase);
        if (safe.Length > maxChars) safe = safe[..maxChars];
        return $"<untrusted_email>\n{safe}\n</untrusted_email>";
    }
}
