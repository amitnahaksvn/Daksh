using System.Text.RegularExpressions;
using Daksh.Ai;
using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>How Daksha treats one specific contact or group — overrides the connection-wide defaults.</summary>
[BsonIgnoreExtraElements]
public sealed class WhatsAppChatRule
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string ChatId { get; set; }
    /// <summary>False = Daksha ignores this chat entirely: no notifications, no auto-reply, no rules.</summary>
    public bool Monitor { get; set; } = true;
    /// <summary>"Default" follows the connection's auto-reply settings; "Always"/"Never" override them.</summary>
    public string AutoReply { get; set; } = "Default";
    /// <summary>"High" chats always notify (even under filtered notifications) and match priority conditions.</summary>
    public string Priority { get; set; } = "Normal";
    public bool AiExcluded { get; set; }
    /// <summary>A fixed reply used for this chat instead of the AI.</summary>
    public string? CustomReply { get; set; }
}

public sealed class WhatsAppRuleAction
{
    /// <summary>AiReply | FixedReply | CreateReminder | ExtractActionItem | Notify | Summarize | SaveMessage | ForwardToTasks</summary>
    public required string Type { get; set; }
    public string? Text { get; set; }
}

/// <summary>A user-defined WHEN / IF / THEN rule.</summary>
[BsonIgnoreExtraElements]
public sealed class WhatsAppAutomationRule
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>NewMessage | FromContact | InGroup | ContainsKeyword | ContainsQuestion | HasAttachment | OutsideHours</summary>
    public string Trigger { get; set; } = "NewMessage";
    /// <summary>IF the chat is one of these (for FromContact / InGroup, empty means any contact / any group).</summary>
    public List<string> ChatIds { get; set; } = [];
    public string? Keyword { get; set; }
    /// <summary>IF the message carries this detection tag (question, payment, meeting, ...).</summary>
    public string? Category { get; set; }
    /// <summary>"Any" or "High" (the chat is marked high priority).</summary>
    public string Priority { get; set; } = "Any";
    public List<WhatsAppRuleAction> Actions { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

/// <summary>Something Daksha pulled out of a message: a detected item (question, payment, meeting...), a task,
/// a reminder, a conversation summary, or a message the user saved.</summary>
[BsonIgnoreExtraElements]
public sealed class WhatsAppInsight
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string ConnectionId { get; set; }
    public required string ChatId { get; set; }
    public string? ChatName { get; set; }
    /// <summary>A detection tag, or Task | Reminder | Summary | Saved.</summary>
    public required string Kind { get; set; }
    public required string Text { get; set; }
    public string? Value { get; set; }
    public DateTime? DueAt { get; set; }
    public bool Done { get; set; }
    public bool Notified { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record ChatRuleDto(string ChatId, bool Monitor, string AutoReply, string Priority, bool AiExcluded, string? CustomReply);
public sealed record UpsertChatRuleRequest(bool? Monitor, string? AutoReply, string? Priority, bool? AiExcluded, string? CustomReply);
public sealed record RuleActionDto(string Type, string? Text);
public sealed record AutomationRuleDto(string Id, string Name, bool Enabled, string Trigger, IReadOnlyList<string> ChatIds, string? Keyword, string? Category, string Priority, IReadOnlyList<RuleActionDto> Actions);
public sealed record SaveAutomationRuleRequest(string? Name, bool? Enabled, string? Trigger, List<string>? ChatIds, string? Keyword, string? Category, string? Priority, List<RuleActionDto>? Actions);
public sealed record InsightDto(string Id, string ChatId, string? ChatName, string Kind, string Text, string? Value, DateTime? DueAt, bool Done, DateTime CreatedAt);

/// <summary>Pattern-based detection of what a message is about. Deliberately simple keyword/regex matching —
/// cheap enough to run on every message with no AI call — so it can miss things and over-match; the UI says so.</summary>
public static partial class WhatsAppIntel
{
    /// <summary>Every detection category, as shown (and switched on/off) in Message Intelligence.</summary>
    public static readonly string[] AllTags =
    [
        "question", "task", "meeting", "appointment", "deadline", "reminder", "payment", "transaction",
        "address", "contact", "document", "delivery", "travel", "job", "announcement",
        "otp", "link", "suspicious-link", "attachment",
    ];

    /// <summary>Categories whose content is worth keeping as an insight. OTPs are deliberately never stored.</summary>
    public static readonly HashSet<string> Extractable =
    [
        "question", "task", "meeting", "appointment", "deadline", "reminder", "payment", "transaction",
        "address", "contact", "document", "delivery", "travel", "job", "announcement",
    ];

    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)] private static partial Regex LinkRx();
    [GeneratedRegex(@"\b(bit\.ly|tinyurl\.com|t\.co|goo\.gl|is\.gd|rb\.gy|cutt\.ly|shorturl\.at)/|\.(xyz|top|click|tk|ml|ga|cf|gq|buzz)\b", RegexOptions.IgnoreCase)] private static partial Regex ShortLinkRx();
    [GeneratedRegex(@"\b(kyc|account.{0,15}(blocked|suspended|expire)|verify your|claim your|prize|lottery|you have won|urgent action)\b", RegexOptions.IgnoreCase)] private static partial Regex PhishingRx();
    [GeneratedRegex(@"\b(otp|one[- ]?time|verification code|passcode|security code|login code)\b", RegexOptions.IgnoreCase)] private static partial Regex OtpWordRx();
    [GeneratedRegex(@"\b\d{4,8}\b")] private static partial Regex CodeRx();
    [GeneratedRegex(@"(\+?\d[\d\s\-]{8,}\d)")] private static partial Regex PhoneRx();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex EmailRx();
    [GeneratedRegex(@"(₹|rs\.?|inr|\$|usd|eur|€)\s?\d[\d,]*(\.\d+)?|\b\d[\d,]*(\.\d+)?\s?(rupees|rs|inr|usd|dollars)\b", RegexOptions.IgnoreCase)] private static partial Regex AmountRx();
    [GeneratedRegex(@"\b\d{1,2}(:\d{2})?\s?(am|pm)\b|\b(today|tonight|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b|\b\d{1,2}[/\-]\d{1,2}([/\-]\d{2,4})?\b", RegexOptions.IgnoreCase)] private static partial Regex WhenRx();
    [GeneratedRegex(@"^\s*(what|when|where|why|how|who|which|can you|could you|will you|would you|do you|are you|is it|did you|have you|kya|kab|kahan|kaise|kyun|kaun)\b", RegexOptions.IgnoreCase)] private static partial Regex QuestionStartRx();

    private static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static List<string> Detect(string text, IReadOnlyCollection<string> disabled)
    {
        var tags = new List<string>();
        void Add(string tag, bool hit) { if (hit && !disabled.Contains(tag)) tags.Add(tag); }

        var isAttachment = text.StartsWith("📷", StringComparison.Ordinal) || text.StartsWith("🎥", StringComparison.Ordinal)
            || text.StartsWith("🎤", StringComparison.Ordinal) || text.StartsWith("📄", StringComparison.Ordinal) || text == "Sticker";
        var hasLink = LinkRx().IsMatch(text);

        Add("attachment", isAttachment);
        Add("document", text.StartsWith("📄", StringComparison.Ordinal));
        Add("link", hasLink);
        Add("suspicious-link", hasLink && (ShortLinkRx().IsMatch(text) || PhishingRx().IsMatch(text)));
        Add("otp", OtpWordRx().IsMatch(text) && CodeRx().IsMatch(text));
        Add("question", text.Contains('?') || QuestionStartRx().IsMatch(text));
        Add("task", Has(text, "please ", "pls ", "need to", "need you to", "make sure", "don't forget", "dont forget", "remember to", "todo", "kindly "));
        Add("meeting", Has(text, "meeting", "zoom", "google meet", "teams call", "catch up", "let's meet", "lets meet", "call at", "join the call"));
        Add("appointment", Has(text, "appointment", "booking", "booked", "reservation", "doctor", "dentist", "your slot"));
        Add("deadline", Has(text, "deadline", "due by", "due on", "due date", "by eod", "asap", "urgent", "last date", "before tomorrow"));
        Add("reminder", Has(text, "remind me", "reminder", "don't let me forget"));
        Add("payment", AmountRx().IsMatch(text) || Has(text, "invoice", "upi", "gpay", "paytm", "phonepe", "send money", "pay me", "payment due", "you owe"));
        Add("transaction", Has(text, "debited", "credited", "transaction", "txn", "utr", "ref no", "a/c"));
        Add("address", Has(text, "address", "pincode", "pin code", "flat no", "house no", "apartment", "street", "road,", "nagar", "layout"));
        Add("contact", PhoneRx().IsMatch(text) || EmailRx().IsMatch(text));
        Add("delivery", Has(text, "out for delivery", "delivered", "shipped", "tracking", "courier", "awb", "dispatched", "your order"));
        Add("travel", Has(text, "flight", "pnr", "boarding pass", "train", "ticket", "hotel", "check-in", "check in", "airport", "itinerary"));
        Add("job", Has(text, "hiring", "job opening", "vacancy", "recruit", "interview", "resume", "opportunity"));
        Add("announcement", Has(text, "announce", "important notice", "attention", "circular", "please note", "notice:"));
        return tags;
    }

    /// <summary>A short, human-readable value pulled out of the text for a tag (amounts, phone numbers, times...).</summary>
    public static string? ExtractValue(string tag, string text)
    {
        static string? Join(MatchCollection m) => m.Count == 0 ? null : string.Join(", ", m.Select(x => x.Value.Trim()).Distinct().Take(4));
        return tag switch
        {
            "payment" or "transaction" => Join(AmountRx().Matches(text)),
            "contact" => Join(PhoneRx().Matches(text)) ?? Join(EmailRx().Matches(text)),
            "meeting" or "appointment" or "deadline" or "reminder" or "delivery" or "travel" => Join(WhenRx().Matches(text)),
            _ => null,
        };
    }

    [GeneratedRegex(@"in (\d+)\s*(minute|min|hour|hr)s?", RegexOptions.IgnoreCase)] private static partial Regex InRx();

    /// <summary>When a reminder created from this message should fire: "in 2 hours", "tomorrow", else an hour from now.</summary>
    public static DateTime ReminderDue(string text)
    {
        var m = InRx().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return DateTime.UtcNow.AddMinutes(m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? n * 60 : n);
        if (text.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow.AddDays(1);
        return DateTime.UtcNow.AddHours(1);
    }
}

public static class WhatsAppAutomationModule
{
    public static readonly string[] Triggers = ["NewMessage", "FromContact", "InGroup", "ContainsKeyword", "ContainsQuestion", "HasAttachment", "OutsideHours"];
    public static readonly string[] ActionTypes = ["AiReply", "FixedReply", "CreateReminder", "ExtractActionItem", "Notify", "Summarize", "SaveMessage", "ForwardToTasks"];

    public static void MapWhatsAppAutomation(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/whatsapp/{connectionId}").WithTags("WhatsAppAutomation").RequireAuthorization(AuthSetup.UserScheme);
        g.MapGet("/chat-rules", ListChatRulesAsync);
        g.MapPut("/chat-rules/{chatId}", UpsertChatRuleAsync);
        g.MapGet("/automation-rules", ListRulesAsync);
        g.MapPost("/automation-rules", CreateRuleAsync);
        g.MapPut("/automation-rules/{ruleId}", UpdateRuleAsync);
        g.MapDelete("/automation-rules/{ruleId}", DeleteRuleAsync);
        g.MapGet("/insights", ListInsightsAsync);
        g.MapPost("/insights/{insightId}/toggle-done", ToggleInsightDoneAsync);
        g.MapDelete("/insights/{insightId}", DeleteInsightAsync);
    }

    private static Task<IntegrationConnection?> OwnedAsync(IMongoDatabase db, string connectionId, string userId, CancellationToken ct) =>
        db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == connectionId && c.UserId == userId).FirstOrDefaultAsync(ct)!;

    // ---- per-chat rules -------------------------------------------------------------------------------------

    private static async Task<IResult> ListChatRulesAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var rules = await db.GetCollection<WhatsAppChatRule>("whatsappChatRules").Find(r => r.ConnectionId == connectionId).ToListAsync(ct);
        return Results.Ok(rules.Select(r => new ChatRuleDto(r.ChatId, r.Monitor, r.AutoReply, r.Priority, r.AiExcluded, r.CustomReply)));
    }

    private static async Task<IResult> UpsertChatRuleAsync(string connectionId, string chatId, UpsertChatRuleRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (req.AutoReply is not null and not ("Default" or "Always" or "Never")) return Results.Problem(statusCode: 400, title: "AutoReply must be Default, Always or Never.");
        if (req.Priority is not null and not ("Normal" or "High")) return Results.Problem(statusCode: 400, title: "Priority must be Normal or High.");

        var rules = db.GetCollection<WhatsAppChatRule>("whatsappChatRules");
        var rule = await rules.Find(r => r.ConnectionId == connectionId && r.ChatId == chatId).FirstOrDefaultAsync(ct)
            ?? new WhatsAppChatRule { ConnectionId = connectionId, ChatId = chatId };
        if (req.Monitor is { } m) rule.Monitor = m;
        if (req.AutoReply is { } a) rule.AutoReply = a;
        if (req.Priority is { } p) rule.Priority = p;
        if (req.AiExcluded is { } x) rule.AiExcluded = x;
        if (req.CustomReply is not null) rule.CustomReply = string.IsNullOrWhiteSpace(req.CustomReply) ? null : req.CustomReply.Trim();

        await rules.ReplaceOneAsync(r => r.Id == rule.Id, rule, new ReplaceOptions { IsUpsert = true }, ct);
        return Results.Ok(new ChatRuleDto(rule.ChatId, rule.Monitor, rule.AutoReply, rule.Priority, rule.AiExcluded, rule.CustomReply));
    }

    public static Task<WhatsAppChatRule?> GetChatRuleAsync(IMongoDatabase db, string connectionId, string chatId, CancellationToken ct) =>
        db.GetCollection<WhatsAppChatRule>("whatsappChatRules").Find(r => r.ConnectionId == connectionId && r.ChatId == chatId).FirstOrDefaultAsync(ct)!;

    // ---- automation rules -----------------------------------------------------------------------------------

    private static AutomationRuleDto ToDto(WhatsAppAutomationRule r) =>
        new(r.Id, r.Name, r.Enabled, r.Trigger, r.ChatIds, r.Keyword, r.Category, r.Priority, r.Actions.Select(a => new RuleActionDto(a.Type, a.Text)).ToList());

    private static async Task<IResult> ListRulesAsync(string connectionId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var rules = await db.GetCollection<WhatsAppAutomationRule>("whatsappAutomationRules").Find(r => r.ConnectionId == connectionId).SortBy(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(rules.Select(ToDto));
    }

    /// <summary>Returns an error message if the request isn't a coherent rule, else null.</summary>
    private static string? Validate(SaveAutomationRuleRequest req, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(req.Name)) return "A rule needs a name.";
        if (req.Name is { Length: > 80 }) return "Rule names are limited to 80 characters.";
        if (req.Trigger is not null && !Triggers.Contains(req.Trigger)) return "Unknown trigger.";
        if (req.Category is { Length: > 0 } c && !WhatsAppIntel.AllTags.Contains(c)) return "Unknown category.";
        if (req.Priority is not null and not ("Any" or "High")) return "Priority must be Any or High.";
        if (req.Trigger == "ContainsKeyword" && string.IsNullOrWhiteSpace(req.Keyword)) return "A keyword trigger needs a keyword.";
        if (creating && (req.Actions is null || req.Actions.Count == 0)) return "A rule needs at least one action.";
        foreach (var a in req.Actions ?? [])
        {
            if (!ActionTypes.Contains(a.Type)) return $"Unknown action \"{a.Type}\".";
            if (a.Type == "FixedReply" && string.IsNullOrWhiteSpace(a.Text)) return "A fixed-reply action needs the reply text.";
        }
        return null;
    }

    private static async Task<IResult> CreateRuleAsync(string connectionId, SaveAutomationRuleRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (Validate(req, creating: true) is { } error) return Results.Problem(statusCode: 400, title: error);

        var rule = new WhatsAppAutomationRule { ConnectionId = connectionId, Name = req.Name!.Trim(), CreatedAt = DateTime.UtcNow };
        Apply(rule, req);
        await db.GetCollection<WhatsAppAutomationRule>("whatsappAutomationRules").InsertOneAsync(rule, cancellationToken: ct);
        return Results.Ok(ToDto(rule));
    }

    private static async Task<IResult> UpdateRuleAsync(string connectionId, string ruleId, SaveAutomationRuleRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        if (Validate(req, creating: false) is { } error) return Results.Problem(statusCode: 400, title: error);

        var rules = db.GetCollection<WhatsAppAutomationRule>("whatsappAutomationRules");
        var rule = await rules.Find(r => r.Id == ruleId && r.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
        if (rule is null) return Results.NotFound();
        Apply(rule, req);
        await rules.ReplaceOneAsync(r => r.Id == ruleId, rule, cancellationToken: ct);
        return Results.Ok(ToDto(rule));
    }

    private static void Apply(WhatsAppAutomationRule rule, SaveAutomationRuleRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Name)) rule.Name = req.Name.Trim();
        if (req.Enabled is { } e) rule.Enabled = e;
        if (req.Trigger is { } t) rule.Trigger = t;
        if (req.ChatIds is { } ids) rule.ChatIds = ids;
        if (req.Keyword is not null) rule.Keyword = string.IsNullOrWhiteSpace(req.Keyword) ? null : req.Keyword.Trim();
        if (req.Category is not null) rule.Category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category;
        if (req.Priority is { } p) rule.Priority = p;
        if (req.Actions is { } actions) rule.Actions = actions.Select(a => new WhatsAppRuleAction { Type = a.Type, Text = a.Text }).ToList();
    }

    private static async Task<IResult> DeleteRuleAsync(string connectionId, string ruleId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        await db.GetCollection<WhatsAppAutomationRule>("whatsappAutomationRules").DeleteOneAsync(r => r.Id == ruleId && r.ConnectionId == connectionId, ct);
        return Results.NoContent();
    }

    // ---- insights -------------------------------------------------------------------------------------------

    private static async Task<IResult> ListInsightsAsync(string connectionId, string? kind, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var filter = Builders<WhatsAppInsight>.Filter.Eq(i => i.ConnectionId, connectionId);
        if (!string.IsNullOrEmpty(kind)) filter &= Builders<WhatsAppInsight>.Filter.Eq(i => i.Kind, kind);
        var items = await db.GetCollection<WhatsAppInsight>("whatsappInsights").Find(filter).SortByDescending(i => i.CreatedAt).Limit(100).ToListAsync(ct);
        return Results.Ok(items.Select(i => new InsightDto(i.Id, i.ChatId, i.ChatName, i.Kind, i.Text, i.Value, i.DueAt, i.Done, i.CreatedAt)));
    }

    private static async Task<IResult> ToggleInsightDoneAsync(string connectionId, string insightId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        var items = db.GetCollection<WhatsAppInsight>("whatsappInsights");
        var item = await items.Find(i => i.Id == insightId && i.ConnectionId == connectionId).FirstOrDefaultAsync(ct);
        if (item is null) return Results.NotFound();
        await items.UpdateOneAsync(i => i.Id == insightId, Builders<WhatsAppInsight>.Update.Set(i => i.Done, !item.Done), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteInsightAsync(string connectionId, string insightId, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await OwnedAsync(db, connectionId, who.UserId, ct) is null) return Results.NotFound();
        await db.GetCollection<WhatsAppInsight>("whatsappInsights").DeleteOneAsync(i => i.Id == insightId && i.ConnectionId == connectionId, ct);
        return Results.NoContent();
    }

    // ---- rule engine ----------------------------------------------------------------------------------------

    /// <summary>The enabled rules whose WHEN trigger and IF conditions all match this incoming message.</summary>
    public static async Task<List<WhatsAppAutomationRule>> MatchRulesAsync(
        IMongoDatabase db, string connectionId, string chatId, bool isGroup, string text, IReadOnlyCollection<string> tags,
        bool highPriority, bool outsideHours, CancellationToken ct)
    {
        var rules = await db.GetCollection<WhatsAppAutomationRule>("whatsappAutomationRules")
            .Find(r => r.ConnectionId == connectionId && r.Enabled).SortBy(r => r.CreatedAt).ToListAsync(ct);

        bool Matches(WhatsAppAutomationRule r)
        {
            var triggerOk = r.Trigger switch
            {
                "FromContact" => !isGroup && (r.ChatIds.Count == 0 || r.ChatIds.Contains(chatId)),
                "InGroup" => isGroup && (r.ChatIds.Count == 0 || r.ChatIds.Contains(chatId)),
                "ContainsKeyword" => !string.IsNullOrWhiteSpace(r.Keyword) && text.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase),
                "ContainsQuestion" => tags.Contains("question"),
                "HasAttachment" => tags.Contains("attachment"),
                "OutsideHours" => outsideHours,
                _ => true,
            };
            if (!triggerOk) return false;
            if (r.Trigger is not ("FromContact" or "InGroup") && r.ChatIds.Count > 0 && !r.ChatIds.Contains(chatId)) return false;
            if (r.Trigger != "ContainsKeyword" && !string.IsNullOrWhiteSpace(r.Keyword) && !text.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(r.Category) && !tags.Contains(r.Category)) return false;
            if (r.Priority == "High" && !highPriority) return false;
            return true;
        }

        return rules.Where(Matches).ToList();
    }

    public static Task AddInsightAsync(IMongoDatabase db, WhatsAppInsight insight, CancellationToken ct) =>
        db.GetCollection<WhatsAppInsight>("whatsappInsights").InsertOneAsync(insight, cancellationToken: ct);

    /// <summary>Runs the non-reply actions of every matched rule. Reply actions (AiReply / FixedReply) are handled
    /// by the caller since they go through the shared send path; this returns which replies were asked for.</summary>
    public static async Task RunStoreActionsAsync(
        IMongoDatabase db, IAssistantOrchestrator orchestrator, UserAiContext userContext, IEnumerable<WhatsAppAutomationRule> matched,
        string connectionId, string chatId, string? chatName, string text, ILogger logger, CancellationToken ct)
    {
        foreach (var action in matched.SelectMany(r => r.Actions))
        {
            try
            {
                switch (action.Type)
                {
                    case "SaveMessage":
                        await AddInsightAsync(db, new WhatsAppInsight { ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Saved", Text = Trim(text), CreatedAt = DateTime.UtcNow }, ct);
                        break;
                    case "ForwardToTasks":
                        await AddInsightAsync(db, new WhatsAppInsight { ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Task", Text = Trim(text), CreatedAt = DateTime.UtcNow }, ct);
                        break;
                    case "CreateReminder":
                        await AddInsightAsync(db, new WhatsAppInsight
                        {
                            ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Reminder",
                            Text = string.IsNullOrWhiteSpace(action.Text) ? Trim(text) : action.Text!,
                            DueAt = WhatsAppIntel.ReminderDue(text), CreatedAt = DateTime.UtcNow,
                        }, ct);
                        break;
                    case "ExtractActionItem":
                    {
                        var task = await AskAsync(orchestrator, userContext,
                            "Extract the single action item or task this WhatsApp message asks of me, as one short imperative sentence. " +
                            $"If there is no action item, output exactly NONE. Message: \"{text}\"", ct) ?? Trim(text);
                        if (!task.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                            await AddInsightAsync(db, new WhatsAppInsight { ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Task", Text = Trim(task), CreatedAt = DateTime.UtcNow }, ct);
                        break;
                    }
                    case "Summarize":
                    {
                        var recent = await db.GetCollection<WhatsAppMessage>("whatsappMessages").Find(m => m.ConnectionId == connectionId && m.ChatId == chatId)
                            .SortByDescending(m => m.Timestamp).Limit(30).ToListAsync(ct);
                        recent.Reverse();
                        var transcript = string.Join("\n", recent.Select(m => $"{(m.FromMe ? "Me" : "Them")}: {m.Text}"));
                        var summary = await AskAsync(orchestrator, userContext,
                            "Summarize this WhatsApp conversation in two or three short sentences, keeping names, commitments and open questions. " +
                            "Output only the summary.\n\n" + transcript, ct);
                        if (summary is not null)
                            await AddInsightAsync(db, new WhatsAppInsight { ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Summary", Text = summary, CreatedAt = DateTime.UtcNow }, ct);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WhatsApp automation action {Action} failed for connection {ConnectionId}", action.Type, connectionId);
            }
        }
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static async Task<string?> AskAsync(IAssistantOrchestrator orchestrator, UserAiContext userContext, string prompt, CancellationToken ct)
    {
        var result = await orchestrator.RespondAsync(new AssistantRequest(userContext, prompt), ct);
        return result.IsSimulated || string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
    }
}

/// <summary>Delivers reminders created by automation rules: when one comes due, push it to the owner's open
/// browser tabs over SignalR and mark it notified so it fires once.</summary>
public sealed class WhatsAppReminderService(IMongoDatabase db, IHubContext<IntegrationsHub> hub, ILogger<WhatsAppReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "WhatsApp reminder delivery pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverDueAsync(CancellationToken ct)
    {
        var insights = db.GetCollection<WhatsAppInsight>("whatsappInsights");
        var due = await insights.Find(i => i.Kind == "Reminder" && !i.Done && !i.Notified && i.DueAt != null && i.DueAt <= DateTime.UtcNow).Limit(50).ToListAsync(ct);
        foreach (var reminder in due)
        {
            var connection = await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == reminder.ConnectionId).FirstOrDefaultAsync(ct);
            if (connection is not null)
                await IntegrationEvents.PublishAsync(hub, connection.UserId, IntegrationEvents.ReminderDue,
                    new { connectionId = reminder.ConnectionId, chatId = reminder.ChatId, chatName = reminder.ChatName, text = reminder.Text, dueAt = reminder.DueAt }, ct);
            await insights.UpdateOneAsync(i => i.Id == reminder.Id, Builders<WhatsAppInsight>.Update.Set(i => i.Notified, true), cancellationToken: ct);
        }
    }
}
