using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Daksh.Platform.Security;

namespace Daksh.Api.Modules;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// OAuth scopes. Daksha requests the narrowest scope per feature (incremental authorization).
// ─────────────────────────────────────────────────────────────────────────────────────────────
public static class GmailScopes
{
    public const string ReadOnly = "https://www.googleapis.com/auth/gmail.readonly";
    public const string Send = "https://www.googleapis.com/auth/gmail.send";
    public const string Compose = "https://www.googleapis.com/auth/gmail.compose";
    public const string Modify = "https://www.googleapis.com/auth/gmail.modify";
    public const string Labels = "https://www.googleapis.com/auth/gmail.labels";
    public const string SettingsBasic = "https://www.googleapis.com/auth/gmail.settings.basic";
    /// <summary>Only scope that allows permanent deletion. Restricted: needs Google security assessment.</summary>
    public const string Full = "https://mail.google.com/";

    /// <summary>What every account is connected with: read + send.</summary>
    public static readonly string[] Base = [ReadOnly, Send];

    /// <summary>Optional feature groups the UI can request, and the single scope each one adds.</summary>
    public static readonly IReadOnlyDictionary<string, string> Features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["compose"] = Compose,
        ["modify"] = Modify,
        ["labels"] = Labels,
        ["settings"] = SettingsBasic,
        ["full"] = Full,
    };

    /// <summary>Space-delimited scope string for the base scopes plus the requested feature groups. Unknown names are ignored.</summary>
    public static string ForFeatures(string? features)
    {
        var scopes = new List<string>(Base);
        foreach (var name in (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Features.TryGetValue(name, out var scope) && !scopes.Contains(scope)) scopes.Add(scope);
        return string.Join(' ', scopes);
    }

    public static string[] Parse(string? granted) =>
        string.IsNullOrWhiteSpace(granted) ? Base : granted.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when <paramref name="granted"/> covers <paramref name="required"/> (gmail.modify implies the narrower read/compose/send/labels scopes).</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required)
    {
        var set = new HashSet<string>(granted, StringComparer.Ordinal);
        if (set.Contains(required) || set.Contains(Full)) return true;
        return set.Contains(Modify) && required is ReadOnly or Send or Compose or Labels;
    }

    public static string Describe(string scope) => scope switch
    {
        ReadOnly => "Read your email so Daksha can show, search and summarise it.",
        Send => "Send email on your behalf when you ask Daksha to.",
        Compose => "Create, edit and send drafts.",
        Modify => "Archive, trash, star, mark read/unread and label messages.",
        Labels => "Create, rename and delete Gmail labels.",
        SettingsBasic => "Read and change Gmail filters, vacation responder and forwarding.",
        Full => "Permanently delete messages. Restricted scope — only request if you need permanent deletion.",
        _ => scope,
    };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Granular permissions and the three profiles. Enforced by the backend in GmailGuard.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public static class GmailPermission
{
    public const string Read = "read", Search = "search", ReadAttachments = "readAttachments", CreateDrafts = "createDrafts",
        Send = "send", Reply = "reply", Forward = "forward", Archive = "archive", MarkRead = "markRead", Star = "star",
        ApplyLabels = "applyLabels", CreateLabels = "createLabels", CreateFilters = "createFilters", DeleteMessages = "deleteMessages",
        PermanentDelete = "permanentDelete", ManageSettings = "manageSettings", RunRules = "runRules",
        AiAutomations = "aiAutomations", ExternalAi = "externalAi";

    public static readonly string[] All =
    [
        Read, Search, ReadAttachments, CreateDrafts, Send, Reply, Forward, Archive, MarkRead, Star, ApplyLabels, CreateLabels,
        CreateFilters, DeleteMessages, PermanentDelete, ManageSettings, RunRules, AiAutomations, ExternalAi,
    ];

    /// <summary>The OAuth scope a permission needs on top of being allowed.</summary>
    public static string? RequiredScope(string permission) => permission switch
    {
        Read or Search or ReadAttachments => GmailScopes.ReadOnly,
        CreateDrafts => GmailScopes.Compose,
        Send or Reply or Forward => GmailScopes.Send,
        Archive or MarkRead or Star or ApplyLabels or DeleteMessages => GmailScopes.Modify,
        CreateLabels => GmailScopes.Labels,
        CreateFilters or ManageSettings => GmailScopes.SettingsBasic,
        PermanentDelete => GmailScopes.Full,
        _ => null, // RunRules / AiAutomations / ExternalAi are Daksha-side permissions; each action still checks its own scope.
    };

    public static string Label(string permission) => permission switch
    {
        Read => "read messages", Search => "search messages", ReadAttachments => "read attachments", CreateDrafts => "create drafts",
        Send => "send messages", Reply => "reply to messages", Forward => "forward messages", Archive => "archive", MarkRead => "mark read/unread",
        Star => "star messages", ApplyLabels => "apply labels", CreateLabels => "create labels", CreateFilters => "create filters",
        DeleteMessages => "move messages to trash", PermanentDelete => "permanently delete messages",
        ManageSettings => "manage Gmail account settings", RunRules => "run automatic rules",
        AiAutomations => "run AI automations", ExternalAi => "send email content to external AI providers",
        _ => permission,
    };
}

public static class GmailProfiles
{
    public const string ReadOnly = "ReadOnly", Assisted = "Assisted", Autonomous = "Autonomous";
    public static readonly string[] Names = [ReadOnly, Assisted, Autonomous];

    private static readonly HashSet<string> ReadOnlySet = [GmailPermission.Read, GmailPermission.Search, GmailPermission.ReadAttachments];

    private static readonly HashSet<string> AssistedSet =
    [
        ..ReadOnlySet, GmailPermission.CreateDrafts, GmailPermission.Send, GmailPermission.Reply, GmailPermission.Forward,
        GmailPermission.Archive, GmailPermission.MarkRead, GmailPermission.Star, GmailPermission.ApplyLabels, GmailPermission.CreateLabels,
    ];

    // Autonomous adds the ability for *rules/automations* to act on their own plus destructive-ish tools.
    // PermanentDelete and ExternalAi are never part of a profile: they need an explicit per-account override.
    private static readonly HashSet<string> AutonomousSet =
    [
        ..AssistedSet, GmailPermission.CreateFilters, GmailPermission.DeleteMessages, GmailPermission.ManageSettings,
        GmailPermission.RunRules, GmailPermission.AiAutomations,
    ];

    public static IReadOnlySet<string> Permissions(string profile) => profile switch
    {
        ReadOnly => ReadOnlySet,
        Autonomous => AutonomousSet,
        _ => AssistedSet,
    };
}

public static class GmailPermissionPolicy
{
    /// <summary>A per-permission override wins over the profile; otherwise the profile decides.</summary>
    public static bool IsAllowed(string profile, IReadOnlyDictionary<string, bool>? overrides, string permission) =>
        overrides is not null && overrides.TryGetValue(permission, out var forced) ? forced : GmailProfiles.Permissions(profile).Contains(permission);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Stored documents
// ─────────────────────────────────────────────────────────────────────────────────────────────
[BsonIgnoreExtraElements]
public sealed class GmailAccountDoc
{
    [BsonId] public required string ConnectionId { get; set; }
    public required string UserId { get; set; }
    public string Email { get; set; } = "";
    public string Nickname { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Space-delimited scopes Google actually granted. Null on accounts connected before scopes were tracked (read+send).</summary>
    public string? GrantedScopes { get; set; }
    public bool IsWorkspace { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SyncConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 15;
    public bool NearRealTime { get; set; } = true;
    public int InitialPeriodDays { get; set; } = 30; // 0 = all available history
    public List<string> Labels { get; set; } = ["INBOX", "SENT"];
    public bool IncludeSpam { get; set; }
    public bool IncludeTrash { get; set; }
    public bool SyncAttachments { get; set; }
    public int MaxAttachmentMb { get; set; } = 10;
    public List<string> ExcludedSenders { get; set; } = [];
    public List<string> ExcludedDomains { get; set; } = [];
    public List<string> ExcludedSubjectKeywords { get; set; } = [];
    public List<string> ExcludedLabels { get; set; } = [];
}

public sealed class AiFeatureConfig
{
    public bool Enabled { get; set; } = true;
    public double ConfidenceThreshold { get; set; } = 0.6;
    public string Priority { get; set; } = "normal"; // low | normal | high
}

public sealed class AiConfig
{
    public bool Enabled { get; set; } = true;
    public string Scope { get; set; } = "all"; // all | important | categories
    public List<string> Categories { get; set; } = [];
    public string SummaryLength { get; set; } = "balanced"; // short | balanced | detailed
    public string Language { get; set; } = "auto"; // auto | en | hi | ...
    public string? ModelId { get; set; }
    /// <summary>Daksha classifies on the server with local heuristics. Email text is only ever sent to an external AI provider when this is true.</summary>
    public bool AllowExternalProviders { get; set; }
    public List<string> ExcludedSenders { get; set; } = [];
    public List<string> ExcludedDomains { get; set; } = [];
    public List<string> ExcludedLabels { get; set; } = [];
    public Dictionary<string, AiFeatureConfig> Features { get; set; } = GmailIntelligence.FeatureIds.ToDictionary(id => id, _ => new AiFeatureConfig());
}

public sealed class ReplyConfig
{
    public bool SuggestionsEnabled { get; set; } = true;
    public bool AutoDraft { get; set; }
    public bool AutoSend { get; set; } // OFF by default; also needs Mode + Autonomous profile
    public string Mode { get; set; } = "AskFirst"; // AskFirst | DraftOnly | AutoRules | AutoCategories
    public List<string> AutoSendCategories { get; set; } = [];
    public bool ReplyToNewMessages { get; set; } = true;
    public bool ReplyToExistingThreads { get; set; } = true;
    public List<string> OnlySenders { get; set; } = [];
    public List<string> ExcludedSenders { get; set; } = [];
    public List<string> ExcludedDomains { get; set; } = [];
    public List<string> ExcludedLabels { get; set; } = [];
    public bool ExcludeAutomated { get; set; } = true;
    public bool ExcludeOtpSecurity { get; set; } = true;
    public bool ExcludeFinancial { get; set; } = true;
    public bool ExcludeLegal { get; set; } = true;
    public bool ExcludeSuspicious { get; set; } = true;
    public string BusinessHoursStart { get; set; } = "09:00";
    public string BusinessHoursEnd { get; set; } = "18:00";
    public bool OnlyInBusinessHours { get; set; }
    public bool ReplyOnWeekends { get; set; }
    public List<string> Holidays { get; set; } = []; // yyyy-MM-dd
    public int ReplyDelayMinutes { get; set; } = 5;
    public int MaxPerHour { get; set; } = 5;
    public int MaxPerDay { get; set; } = 25;
    public double MinConfidence { get; set; } = 0.8;
    public string Style { get; set; } = "Professional"; // Professional | Friendly | Formal | Casual | Short | Detailed | Custom
    public string PersonalInstructions { get; set; } = "";
    public string BusinessInstructions { get; set; } = "";
    public string Greeting { get; set; } = "Hello";
    public string Closing { get; set; } = "Best regards";
    public string Signature { get; set; } = "";
    public string Language { get; set; } = "auto";
    public int MaxLength { get; set; } = 600;
    public List<string> PhrasesToAvoid { get; set; } = [];
    public List<string> NeverCommitTo { get; set; } = ["meetings", "deadlines", "payments", "prices"];
}

public sealed class FollowUpConfig
{
    public bool Enabled { get; set; } = true;
    public List<int> IntervalsDays { get; set; } = [3];
    public bool NotifyBeforeDeadline { get; set; } = true;
    public int DeadlineLeadHours { get; set; } = 24;
    public bool AutoDraft { get; set; }
    /// <summary>Not implemented by design: follow-ups are drafted, never sent without a human. Persisted for the UI to show the limitation.</summary>
    public bool AutoSend { get; set; }
}

public sealed class NotificationConfig
{
    public Dictionary<string, bool> Types { get; set; } = GmailNotificationTypes.All.ToDictionary(t => t, t => GmailNotificationTypes.DefaultOn.Contains(t));
    public string QuietHoursStart { get; set; } = "";
    public string QuietHoursEnd { get; set; } = "";
    public string Digest { get; set; } = "off"; // off | daily | weekly
    public int MaxPerHour { get; set; } = 20;
    public bool Group { get; set; } = true;
}

public sealed class AttachmentConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowedExtensions { get; set; } = [];
    public List<string> ExcludedExtensions { get; set; } = [];
    public int MaxSizeMb { get; set; } = 10;
    public int MaxTotalStorageMb { get; set; } = 500;
    public bool AiAnalysis { get; set; }
    public bool AutoClassify { get; set; } = true;
    public bool DetectDuplicates { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
    public bool DeleteProcessedCopies { get; set; }
}

public sealed class PermissionConfig
{
    public string Profile { get; set; } = GmailProfiles.Assisted;
    /// <summary>permission → forced allow/deny. Overrides the profile either way.</summary>
    public Dictionary<string, bool> Overrides { get; set; } = [];
}

public sealed class PrivacyConfig
{
    /// <summary>Store snippets and subjects of synced messages locally. Message bodies are never stored.</summary>
    public bool StoreSnippets { get; set; } = true;
    public int RetentionDays { get; set; } = 180;
}

[BsonIgnoreExtraElements]
public sealed class GmailAccountConfig
{
    [BsonId] public string ConnectionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int Version { get; set; }
    public SyncConfig Sync { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public ReplyConfig Reply { get; set; } = new();
    public FollowUpConfig FollowUp { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public AttachmentConfig Attachments { get; set; } = new();
    public PermissionConfig Permissions { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; }
}

public static class GmailNotificationTypes
{
    public static readonly string[] All =
    [
        "newEmail", "importantEmail", "urgentEmail", "selectedSender", "invoiceReceived", "paymentReminder", "meetingInvitation",
        "actionRequired", "followUpDue", "deadlineApproaching", "aiProcessingCompleted", "attachmentReceived", "sendFailure",
        "connectionExpired", "syncFailure", "oauthPermissionIssue",
    ];
    public static readonly HashSet<string> DefaultOn = ["importantEmail", "urgentEmail", "followUpDue", "deadlineApproaching", "sendFailure", "connectionExpired", "syncFailure", "oauthPermissionIssue"];
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Validation: every field a user can save is bounded and whitelisted here, server-side.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public static class GmailConfigValidator
{
    private static readonly int[] SyncDays = [0, 7, 30, 90, 180];
    private static readonly string[] ReplyModes = ["AskFirst", "DraftOnly", "AutoRules", "AutoCategories"];
    private static readonly string[] Styles = ["Professional", "Friendly", "Formal", "Casual", "Short", "Detailed", "Custom"];

    /// <summary>Validates and normalises in place. Returns field → messages; empty means valid.</summary>
    public static Dictionary<string, string[]> Validate(GmailAccountConfig c)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string field, string message) => errors[field] = [message];

        c.Sync.IntervalMinutes = Math.Clamp(c.Sync.IntervalMinutes, 5, 1440);
        if (!SyncDays.Contains(c.Sync.InitialPeriodDays)) Err("sync.initialPeriodDays", "Choose 7, 30, 90, 180 days or 0 for all history.");
        c.Sync.MaxAttachmentMb = Math.Clamp(c.Sync.MaxAttachmentMb, 1, 25);
        c.Sync.Labels = Clean(c.Sync.Labels, 50, 100);
        c.Sync.ExcludedSenders = CleanLower(c.Sync.ExcludedSenders);
        c.Sync.ExcludedDomains = CleanLower(c.Sync.ExcludedDomains);
        c.Sync.ExcludedSubjectKeywords = CleanLower(c.Sync.ExcludedSubjectKeywords);
        c.Sync.ExcludedLabels = Clean(c.Sync.ExcludedLabels, 50, 100);

        if (c.Ai.Scope is not ("all" or "important" or "categories")) Err("ai.scope", "Scope must be all, important or categories.");
        if (c.Ai.SummaryLength is not ("short" or "balanced" or "detailed")) Err("ai.summaryLength", "Summary length must be short, balanced or detailed.");
        if (c.Ai.Language.Length is 0 or > 10) Err("ai.language", "Choose a language.");
        c.Ai.Categories = Clean(c.Ai.Categories.Where(GmailIntelligence.FeatureIds.Contains).ToList(), 30, 50);
        c.Ai.ExcludedSenders = CleanLower(c.Ai.ExcludedSenders);
        c.Ai.ExcludedDomains = CleanLower(c.Ai.ExcludedDomains);
        c.Ai.ExcludedLabels = Clean(c.Ai.ExcludedLabels, 50, 100);
        var features = new Dictionary<string, AiFeatureConfig>();
        foreach (var id in GmailIntelligence.FeatureIds)
        {
            var f = c.Ai.Features.TryGetValue(id, out var existing) ? existing : new AiFeatureConfig();
            f.ConfidenceThreshold = Math.Clamp(f.ConfidenceThreshold, 0.0, 1.0);
            if (f.Priority is not ("low" or "normal" or "high")) f.Priority = "normal";
            features[id] = f;
        }
        c.Ai.Features = features;

        if (!ReplyModes.Contains(c.Reply.Mode)) Err("reply.mode", "Unknown approval mode.");
        if (!Styles.Contains(c.Reply.Style)) Err("reply.style", "Unknown reply style.");
        c.Reply.MaxPerHour = Math.Clamp(c.Reply.MaxPerHour, 0, 100);
        c.Reply.MaxPerDay = Math.Clamp(c.Reply.MaxPerDay, 0, 500);
        c.Reply.ReplyDelayMinutes = Math.Clamp(c.Reply.ReplyDelayMinutes, 0, 1440);
        c.Reply.MinConfidence = Math.Clamp(c.Reply.MinConfidence, 0.0, 1.0);
        c.Reply.MaxLength = Math.Clamp(c.Reply.MaxLength, 50, 5000);
        if (!TimeOnly.TryParse(c.Reply.BusinessHoursStart, out _) || !TimeOnly.TryParse(c.Reply.BusinessHoursEnd, out _)) Err("reply.businessHours", "Use HH:mm for business hours.");
        if (c.Reply.PersonalInstructions.Length > 2000 || c.Reply.BusinessInstructions.Length > 2000) Err("reply.instructions", "Instructions can be at most 2,000 characters.");
        if (c.Reply.Signature.Length > 2000) Err("reply.signature", "The signature can be at most 2,000 characters.");
        if (c.Reply.Greeting.Length > 100 || c.Reply.Closing.Length > 100) Err("reply.greeting", "Greeting and closing can be at most 100 characters.");
        c.Reply.AutoSendCategories = Clean(c.Reply.AutoSendCategories.Where(GmailIntelligence.FeatureIds.Contains).ToList(), 30, 50);
        c.Reply.OnlySenders = CleanLower(c.Reply.OnlySenders);
        c.Reply.ExcludedSenders = CleanLower(c.Reply.ExcludedSenders);
        c.Reply.ExcludedDomains = CleanLower(c.Reply.ExcludedDomains);
        c.Reply.ExcludedLabels = Clean(c.Reply.ExcludedLabels, 50, 100);
        c.Reply.PhrasesToAvoid = Clean(c.Reply.PhrasesToAvoid, 50, 200);
        c.Reply.NeverCommitTo = Clean(c.Reply.NeverCommitTo, 50, 200);
        c.Reply.Holidays = c.Reply.Holidays.Where(h => DateOnly.TryParseExact(h, "yyyy-MM-dd", out _)).Distinct().Take(100).ToList();
        // Safety: automatic sending can never be on while the approval mode still says "ask first / drafts only".
        if (c.Reply.Mode is "AskFirst" or "DraftOnly") c.Reply.AutoSend = false;

        c.FollowUp.IntervalsDays = c.FollowUp.IntervalsDays.Where(d => d is >= 1 and <= 90).Distinct().Order().Take(10).ToList();
        if (c.FollowUp.IntervalsDays.Count == 0) c.FollowUp.IntervalsDays = [3];
        c.FollowUp.DeadlineLeadHours = Math.Clamp(c.FollowUp.DeadlineLeadHours, 1, 24 * 14);
        c.FollowUp.AutoSend = false; // see FollowUpConfig.AutoSend

        var types = new Dictionary<string, bool>();
        foreach (var t in GmailNotificationTypes.All)
            types[t] = c.Notifications.Types.TryGetValue(t, out var on) ? on : GmailNotificationTypes.DefaultOn.Contains(t);
        c.Notifications.Types = types;
        if (c.Notifications.Digest is not ("off" or "daily" or "weekly")) Err("notifications.digest", "Digest must be off, daily or weekly.");
        c.Notifications.MaxPerHour = Math.Clamp(c.Notifications.MaxPerHour, 1, 200);
        foreach (var q in new[] { c.Notifications.QuietHoursStart, c.Notifications.QuietHoursEnd })
            if (q.Length > 0 && !TimeOnly.TryParse(q, out _)) Err("notifications.quietHours", "Use HH:mm for quiet hours.");

        c.Attachments.MaxSizeMb = Math.Clamp(c.Attachments.MaxSizeMb, 1, 25);
        c.Attachments.MaxTotalStorageMb = Math.Clamp(c.Attachments.MaxTotalStorageMb, 10, 20_000);
        c.Attachments.RetentionDays = Math.Clamp(c.Attachments.RetentionDays, 1, 3650);
        c.Attachments.AllowedExtensions = CleanExt(c.Attachments.AllowedExtensions);
        c.Attachments.ExcludedExtensions = CleanExt(c.Attachments.ExcludedExtensions);

        if (!GmailProfiles.Names.Contains(c.Permissions.Profile)) Err("permissions.profile", "Choose Read Only, Assisted or Autonomous.");
        c.Permissions.Overrides = c.Permissions.Overrides.Where(kv => GmailPermission.All.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        c.Privacy.RetentionDays = Math.Clamp(c.Privacy.RetentionDays, 1, 3650);
        return errors;
    }

    private static List<string> Clean(List<string>? values, int max, int maxLen) =>
        (values ?? []).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0 && v.Length <= maxLen).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    private static List<string> CleanLower(List<string>? values) => Clean(values, 200, 200).Select(v => v.ToLowerInvariant()).ToList();
    private static List<string> CleanExt(List<string>? values) =>
        Clean(values, 100, 12).Select(v => v.TrimStart('.').ToLowerInvariant()).Where(v => v.All(char.IsLetterOrDigit)).Distinct().ToList();
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// Backend guard: resolves the account, then checks account enabled → permission → OAuth scope.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public sealed record GmailAuthorized(IntegrationConnection Connection, GmailAccountDoc Account, GmailAccountConfig Config);

public static class GmailGuard
{
    public static async Task<GmailAuthorized> AuthorizeAsync(IMongoDatabase db, string userId, string? accountId, string permission, CancellationToken ct)
    {
        var connection = await GmailIntegrationModule.ResolveConnectionAsync(db, userId, accountId, ct)
            ?? throw new GmailIntegrationException(409, "Connect a Gmail account to continue.", "not_connected");
        var account = await GmailIntegrationModule.LoadAccountAsync(db, connection, ct);
        if (!account.Enabled)
            throw new GmailIntegrationException(409, "This Gmail account is disabled in Daksha. Enable it in Gmail settings.", "account_disabled");
        var config = await GmailIntegrationModule.LoadConfigAsync(db, userId, connection.Id, ct);

        if (!GmailPermissionPolicy.IsAllowed(config.Permissions.Profile, config.Permissions.Overrides, permission))
            throw new GmailIntegrationException(403,
                $"Daksha is not allowed to {GmailPermission.Label(permission)} on this account ({config.Permissions.Profile} profile). Change the permission in Gmail Security settings.",
                "permission_denied");

        var scope = GmailPermission.RequiredScope(permission);
        if (scope is not null && !GmailScopes.Satisfies(GmailScopes.Parse(account.GrantedScopes), scope))
            throw new GmailIntegrationException(403,
                $"Google hasn't been asked for permission to {GmailPermission.Label(permission)} yet. Approve it to continue.",
                "scope_upgrade_required", scope);
        return new GmailAuthorized(connection, account, config);
    }
}

public static class GmailBackoff
{
    /// <summary>Exponential backoff with full jitter: ~0.5s, 1s, 2s … capped at 20s.</summary>
    public static TimeSpan Delay(int attempt, Random? random = null)
    {
        var ceiling = Math.Min(20_000, 500 * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds((random ?? Random.Shared).NextDouble() * ceiling * 0.5 + ceiling * 0.5);
    }
}

public static class GmailLocalData
{
    private static readonly string[] AccountCollections =
        ["gmailMessages", "gmailSyncState", "gmailSyncHistory", "gmailRules", "gmailRuleRuns", "gmailFollowUps", "gmailNotifications",
         "gmailAttachments", "gmailSavedSearches", "gmailWebhookEvents", "gmailDeadLetters", "gmailCorrections", "gmailActionItems", "gmailReplyLog"];

    /// <summary>Deletes everything Daksha stored locally about an account (config, synced message metadata, rules, notifications…).
    /// The audit trail is kept by default so a disconnect/purge stays traceable.</summary>
    public static async Task PurgeAccountAsync(IMongoDatabase db, string userId, string connectionId, bool keepAudit, CancellationToken ct)
    {
        foreach (var name in AccountCollections)
            await db.GetCollection<MongoDB.Bson.BsonDocument>(name).DeleteManyAsync(
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", userId) & Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("AccountId", connectionId), ct);
        await db.GetCollection<MongoDB.Bson.BsonDocument>("gmailAccountConfigs").DeleteOneAsync(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", connectionId) & Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", userId), ct);
        await db.GetCollection<MongoDB.Bson.BsonDocument>("gmailAccounts").DeleteOneAsync(
            Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", connectionId) & Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", userId), ct);
        if (!keepAudit)
            await db.GetCollection<MongoDB.Bson.BsonDocument>("gmailActionAudit").DeleteManyAsync(
                Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", userId) & Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("AccountId", connectionId), ct);
    }
}

