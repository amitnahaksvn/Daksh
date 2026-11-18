using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Daksh.Api.Modules;

public sealed class GmailRuleCondition
{
    public string Field { get; set; } = "";
    public string Op { get; set; } = "contains"; // contains | equals | startsWith | endsWith | is
    public string Value { get; set; } = "";
    /// <summary>NOT: the condition passes when it does NOT match.</summary>
    public bool Negate { get; set; }
}

public sealed class GmailRuleAction
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}

[BsonIgnoreExtraElements]
public sealed class GmailRule
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Lower runs first.</summary>
    public int Priority { get; set; } = 100;
    public bool StopProcessing { get; set; }
    /// <summary>"all" = AND, "any" = OR.</summary>
    public string Match { get; set; } = "all";
    public List<GmailRuleCondition> Conditions { get; set; } = [];
    public List<GmailRuleAction> Actions { get; set; } = [];
    /// <summary>Daksha runs the rule automatically on new mail. Needs the Autonomous profile (or the runRules override). Off = manual only.</summary>
    public bool RunAutomatically { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class GmailRuleRun
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string AccountId { get; set; }
    public required string RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public required string MessageId { get; set; }
    public string Trigger { get; set; } = "manual"; // manual | automatic | test
    public List<string> Applied { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public string Status { get; set; } = "ok"; // ok | partial | failed
    public string? Error { get; set; }
    public DateTime AtUtc { get; set; }
}

public sealed record GmailMessageFacts(
    string From, string To, string Cc, string Subject, string Body, bool HasAttachment, IReadOnlyList<string> AttachmentTypes,
    IReadOnlyList<string> Labels, bool Important, bool Starred, IReadOnlyList<string> Categories, bool Urgent, DateTimeOffset Date);

public sealed record GmailRuleEvaluation(bool Matched, IReadOnlyList<string> Reasons);

public static class GmailRuleEngine
{
    public static readonly string[] Fields =
    [
        "from", "fromDomain", "to", "cc", "subject", "body", "hasAttachment", "attachmentType", "label",
        "important", "starred", "category", "urgency", "receivedAfter", "receivedBefore", "keyword",
    ];

    public static readonly string[] ActionTypes =
    [
        "addLabel", "createLabel", "removeLabel", "markRead", "markUnread", "star", "unstar", "markImportant", "archive", "trash",
        "forward", "createTask", "createReminder", "generateDraft", "notify", "runWorkflow",
    ];

    /// <summary>Daksha-side actions (no Gmail call). They never need a Gmail scope.</summary>
    public static readonly HashSet<string> DakshaSideActions = ["createTask", "createReminder", "notify", "runWorkflow"];

    /// <summary>Actions that remove mail from the inbox or are otherwise hard to undo in bulk. They always require explicit confirmation.</summary>
    public static readonly HashSet<string> Destructive = ["trash", "forward"];

    public static string PermissionFor(string actionType) => actionType switch
    {
        "addLabel" or "removeLabel" or "markImportant" => GmailPermission.ApplyLabels,
        "createLabel" => GmailPermission.CreateLabels,
        "markRead" or "markUnread" => GmailPermission.MarkRead,
        "star" or "unstar" => GmailPermission.Star,
        "archive" => GmailPermission.Archive,
        "trash" => GmailPermission.DeleteMessages,
        "forward" => GmailPermission.Forward,
        "generateDraft" => GmailPermission.CreateDrafts,
        _ => GmailPermission.Read,
    };

    public static GmailRuleEvaluation Evaluate(GmailRule rule, GmailMessageFacts facts)
    {
        if (rule.Conditions.Count == 0) return new(false, ["The rule has no conditions."]);
        var reasons = new List<string>();
        var results = new List<bool>();
        foreach (var c in rule.Conditions)
        {
            var hit = Test(c, facts);
            var passed = c.Negate ? !hit : hit;
            results.Add(passed);
            reasons.Add($"{(c.Negate ? "NOT " : "")}{c.Field} {c.Op} \"{Trunc(c.Value)}\" → {(passed ? "match" : "no match")}");
        }
        var matched = rule.Match == "any" ? results.Any(r => r) : results.All(r => r);
        return new(matched, reasons);
    }

    private static bool Test(GmailRuleCondition c, GmailMessageFacts f)
    {
        var v = (c.Value ?? "").Trim();
        bool Text(string haystack) => Compare(haystack, v, c.Op);
        switch (c.Field)
        {
            case "from": return Text(f.From);
            case "fromDomain":
            {
                var addr = GmailIntelligence.ExtractAddress(f.From);
                var domain = addr.Contains('@') ? addr[(addr.IndexOf('@') + 1)..] : "";
                return Compare(domain, v.TrimStart('@'), c.Op == "contains" ? "equals" : c.Op);
            }
            case "to": return Text(f.To);
            case "cc": return Text(f.Cc);
            case "subject": return Text(f.Subject);
            case "body": return Text(f.Body);
            case "keyword": return Text(f.Subject) || Text(f.Body);
            case "hasAttachment": return f.HasAttachment == ParseBool(v);
            case "attachmentType": return f.AttachmentTypes.Any(t => string.Equals(t.TrimStart('.'), v.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
            case "label": return f.Labels.Any(l => string.Equals(l, v, StringComparison.OrdinalIgnoreCase));
            case "important": return f.Important == ParseBool(v);
            case "starred": return f.Starred == ParseBool(v);
            case "category": return f.Categories.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
            case "urgency": return f.Urgent == (v.Equals("urgent", StringComparison.OrdinalIgnoreCase) || ParseBool(v));
            case "receivedAfter": return DateTimeOffset.TryParse(v, out var after) && f.Date >= after;
            case "receivedBefore": return DateTimeOffset.TryParse(v, out var before) && f.Date <= before;
            default: return false;
        }
    }

    private static bool Compare(string haystack, string needle, string op)
    {
        if (needle.Length == 0) return false;
        return op switch
        {
            "equals" or "is" => string.Equals(haystack.Trim(), needle, StringComparison.OrdinalIgnoreCase),
            "startsWith" => haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            "endsWith" => haystack.EndsWith(needle, StringComparison.OrdinalIgnoreCase),
            _ => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool ParseBool(string v) => v.Length == 0 || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Rules that apply to the message, in priority order, honouring "stop processing".</summary>
    public static IReadOnlyList<(GmailRule Rule, GmailRuleEvaluation Result)> MatchingRules(IEnumerable<GmailRule> rules, GmailMessageFacts facts)
    {
        var matches = new List<(GmailRule, GmailRuleEvaluation)>();
        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.CreatedAtUtc))
        {
            var result = Evaluate(rule, facts);
            if (!result.Matched) continue;
            matches.Add((rule, result));
            if (rule.StopProcessing) break;
        }
        return matches;
    }

    public static Dictionary<string, string[]> Validate(GmailRule rule)
    {
        var errors = new Dictionary<string, string[]>();
        rule.Name = (rule.Name ?? "").Trim();
        rule.Description = (rule.Description ?? "").Trim();
        if (rule.Name.Length is 0 or > 100) errors["name"] = ["Give the rule a name (up to 100 characters)."];
        if (rule.Description.Length > 500) errors["description"] = ["The description can be at most 500 characters."];
        if (rule.Match is not ("all" or "any")) errors["match"] = ["Match must be all (AND) or any (OR)."];
        rule.Priority = Math.Clamp(rule.Priority, 1, 10_000);
        if (rule.Conditions.Count is 0 or > 10) errors["conditions"] = ["Add between 1 and 10 conditions."];
        else foreach (var c in rule.Conditions)
        {
            c.Value = (c.Value ?? "").Trim();
            if (!Fields.Contains(c.Field)) { errors["conditions"] = [$"Unknown condition field \"{c.Field}\"."]; break; }
            if (c.Op is not ("contains" or "equals" or "startsWith" or "endsWith" or "is")) { errors["conditions"] = [$"Unknown operator \"{c.Op}\"."]; break; }
            if (c.Value.Length > 300) { errors["conditions"] = ["A condition value can be at most 300 characters."]; break; }
            if (c.Value.Length == 0 && c.Field is not ("hasAttachment" or "important" or "starred")) { errors["conditions"] = [$"Enter a value for \"{c.Field}\"."]; break; }
        }
        if (rule.Actions.Count is 0 or > 10) errors["actions"] = ["Add between 1 and 10 actions."];
        else foreach (var a in rule.Actions)
        {
            a.Value = (a.Value ?? "").Trim();
            if (!ActionTypes.Contains(a.Type)) { errors["actions"] = [$"Unknown action \"{a.Type}\"."]; break; }
            var needsValue = a.Type is "addLabel" or "createLabel" or "removeLabel" or "forward" or "createTask" or "createReminder" or "runWorkflow";
            if (needsValue && a.Value.Length is 0 or > 300) { errors["actions"] = [$"Enter a value for \"{a.Type}\"."]; break; }
            if (a.Type == "forward" && !Regex.IsMatch(a.Value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { errors["actions"] = ["Forward needs a valid email address."]; break; }
            if (a.Type is "addLabel" or "createLabel" && (a.Value.Contains('/') && a.Value.Length > 225)) { errors["actions"] = ["Label names can be at most 225 characters."]; break; }
        }
        return errors;
    }
}

/// <summary>Builds a Gmail search query from a structured form so users never have to know the operator syntax.</summary>
public sealed class GmailSearchCriteria
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? ExactPhrase { get; set; }
    public string? After { get; set; }   // yyyy-MM-dd
    public string? Before { get; set; }  // yyyy-MM-dd
    public bool? HasAttachment { get; set; }
    public string? FileType { get; set; }
    public string? Label { get; set; }
    public bool? IsUnread { get; set; }
    public bool? IsRead { get; set; }
    public bool? IsStarred { get; set; }
    public bool? IsImportant { get; set; }
    public string? In { get; set; } // inbox | sent | drafts | trash | spam | anywhere
    /// <summary>Optional power-user query appended verbatim (still length-limited).</summary>
    public string? Raw { get; set; }
}

public static class GmailSearchBuilder
{
    private static readonly string[] Folders = ["inbox", "sent", "drafts", "trash", "spam", "anywhere", "starred", "important"];

    public static string Build(GmailSearchCriteria c)
    {
        var parts = new List<string>();
        void Add(string op, string? value) { var t = Token(value); if (t.Length > 0) parts.Add($"{op}:{t}"); }
        Add("from", c.From); Add("to", c.To); Add("subject", c.Subject); Add("label", c.Label);
        if (!string.IsNullOrWhiteSpace(c.Keywords)) parts.Add(Clean(c.Keywords, 300));
        if (!string.IsNullOrWhiteSpace(c.ExactPhrase)) parts.Add($"\"{Clean(c.ExactPhrase, 200).Replace("\"", "")}\"");
        if (DateOnly.TryParse(c.After, out var after)) parts.Add($"after:{after:yyyy/MM/dd}");
        if (DateOnly.TryParse(c.Before, out var before)) parts.Add($"before:{before:yyyy/MM/dd}");
        if (c.HasAttachment == true) parts.Add("has:attachment");
        if (!string.IsNullOrWhiteSpace(c.FileType)) Add("filename", "." + c.FileType.Trim().TrimStart('.'));
        if (c.IsUnread == true) parts.Add("is:unread");
        if (c.IsRead == true) parts.Add("is:read");
        if (c.IsStarred == true) parts.Add("is:starred");
        if (c.IsImportant == true) parts.Add("is:important");
        if (c.In is { } folder && Folders.Contains(folder.ToLowerInvariant()))
            parts.Add(folder.Equals("anywhere", StringComparison.OrdinalIgnoreCase) ? "in:anywhere" : folder is "starred" or "important" ? $"is:{folder.ToLowerInvariant()}" : $"in:{folder.ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(c.Raw)) parts.Add(Clean(c.Raw, 500));
        return string.Join(' ', parts);
    }

    /// <summary>A value safe to place after an operator: control characters removed and quoted when it has spaces.</summary>
    private static string Token(string? value)
    {
        var v = Clean(value, 200).Replace("\"", "");
        return v.Length == 0 ? "" : v.Contains(' ') ? $"\"{v}\"" : v;
    }

    private static string Clean(string? value, int max)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? "").Trim())
            if (!char.IsControl(ch)) sb.Append(ch);
        var s = sb.ToString();
        return s.Length > max ? s[..max] : s;
    }
}
