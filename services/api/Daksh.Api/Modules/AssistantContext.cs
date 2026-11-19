using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record AssistantContextResult(string Text, IReadOnlyList<string> Sources);

/// <summary>Works out, from the words of a question, which connected apps are worth looking at. Pure, so it is tested directly.</summary>
public static class AssistantIntent
{
    public sealed record Intent(bool Messages, bool Mail, bool Tasks, bool Overview, IReadOnlyList<string> Names, bool Lists = false, bool Codes = false)
    {
        public bool AnyApp => Messages || Mail || Tasks || Overview || Names.Count > 0 || Lists || Codes;
    }

    private static readonly Regex MessagesRx = new(@"\b(whats?app|telegram|messages?|msgs?|chats?|dms?|texts?|texted|replied|reply|said|say|told|unread|missed|groups?|conversation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MailRx = new(@"\b(mails?|e-?mails?|gmail|inbox|subject|invoices?|newsletters?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TasksRx = new(@"\b(tasks?|to-?dos?|reminders?|remind|due|deadlines?|overdue|pending|follow-?ups?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListsRx = new(@"\b(lists?|shopping|buy|buying|groceries|grocery|amazon|wishlist|wish list|to buy|purchase)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodesRx = new(@"\b(otps?|codes?|verification|passcodes?|2fa|one[- ]time)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OverviewRx = new(@"\b(today|tonight|summary|summari[sz]e|catch me up|catch up|brief(ing)?|what.?s new|anything|everything|miss(ed)?|overview|status|what did i|what do i)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase) { "the", "and", "for", "mr", "mrs", "dr", "my", "our", "group", "team", "family", "office", "work", "new", "mom", "dad" };

    public static Intent Detect(string question, IEnumerable<string> chatNames)
    {
        var q = question ?? "";
        var matched = new List<string>();
        foreach (var name in chatNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var hit = (name.Length >= 3 && ContainsWord(q, name)) || (first.Length >= 3 && !Stop.Contains(first) && ContainsWord(q, first));
            if (hit) matched.Add(name);
            if (matched.Count == 3) break;
        }
        return new Intent(MessagesRx.IsMatch(q), MailRx.IsMatch(q) || CodesRx.IsMatch(q) || Regex.IsMatch(q, @"\b(important|urgent)\b", RegexOptions.IgnoreCase), TasksRx.IsMatch(q), OverviewRx.IsMatch(q), matched, ListsRx.IsMatch(q), CodesRx.IsMatch(q));
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "when", "where", "which", "who", "whom", "whose", "why", "how", "does", "did", "have", "has", "had", "with", "from", "that", "this", "there", "their", "about",
        "tell", "show", "give", "please", "can", "could", "would", "should", "will", "any", "some", "into", "over", "just", "like", "need", "want", "know", "check", "find", "look",
        "been", "were", "was", "are", "you", "your", "mine", "them", "they", "then", "than", "also", "again", "much", "many", "more", "most", "last", "latest", "recent", "daksha",
    };

    /// <summary>The meaningful words of a question (4+ letters, no filler), used to search the apps' text for what was asked about.</summary>
    public static IReadOnlyList<string> Keywords(string question) =>
        Regex.Matches(question ?? "", @"[\p{L}\p{N}]{4,}").Select(m => m.Value).Where(w => !Filler.Contains(w) && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();

    /// <summary>A real question (not "hi" or "thanks"): worth a look at the connected apps even when no app is named.</summary>
    public static bool IsSubstantive(string question) => Regex.Matches(question ?? "", @"[\p{L}\p{N}]+").Count >= 3;

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
}

/// <summary>Gathers, read-only, what the assistant needs from the owner's connected apps to answer one question.</summary>
public static class AssistantContextBuilder
{
    private const int MaxChars = 7000;
    private sealed record ChatInfo(string Source, string Name, int Unread, DateTime LastAt, bool LastFromMe, string LastText, IReadOnlyList<(DateTime At, bool FromMe, string Who, string Text)> Recent);

    public static string Clip(string? text, int max)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    /// <summary>One chat's recent messages, oldest first, one line each, labelled with who said it.</summary>
    public static string FormatChat(string name, IEnumerable<(DateTime At, bool FromMe, string Who, string Text)> messages) =>
        $"Chat with {name}:\n" + string.Join("\n", messages.OrderBy(m => m.At).Select(m => $"[{m.At.ToLocalTime():d MMM HH:mm}] {(m.FromMe ? "Me" : m.Who)}: {Clip(m.Text, 300)}"));

    public static async Task<AssistantContextResult> BuildAsync(IMongoDatabase db, string userId, string question,
        Func<Task<IReadOnlyList<GmailMessageSummary>?>> recentMail, CancellationToken ct)
    {
        var sources = new List<string>();
        var sb = new StringBuilder();
        sb.AppendLine($"Now: {DateTime.UtcNow:ddd d MMM yyyy HH:mm} UTC.");

        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.UserId == userId).ToListAsync(ct);
        var apps = connections.GroupBy(c => c.IntegrationKey).Select(g => $"{g.Key} ({string.Join("/", g.Select(c => c.Status).Distinct())})");
        sb.AppendLine("Connected apps: " + (connections.Count == 0 ? "none" : string.Join(", ", apps)) + ".");

        // Chats (WhatsApp and Telegram), each with a readable name.
        var chats = new List<ChatInfo>();
        foreach (var c in connections.Where(c => c.IntegrationKey == "whatsapp"))
            chats.AddRange(await WhatsAppChatsAsync(db, c, ct));
        foreach (var c in connections.Where(c => c.IntegrationKey == "telegram"))
            chats.AddRange(await TelegramChatsAsync(db, c, ct));
        chats = chats.OrderByDescending(c => c.LastAt).ToList();

        var intent = AssistantIntent.Detect(question, chats.Select(c => c.Name));
        // Anything that could be about the owner's life gets a look at their apps, whether or not an app was named:
        // a real question with no app words falls back to the overview, and the question's own words are searched for below.
        var overview = intent.Overview || (!intent.AnyApp && AssistantIntent.IsSubstantive(question));

        if (intent.Names.Count > 0)
        {
            foreach (var name in intent.Names)
                foreach (var chat in chats.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2))
                {
                    sb.AppendLine().AppendLine(FormatChat($"{chat.Name} ({chat.Source})", chat.Recent.Take(20)));
                    if (!sources.Contains(chat.Source)) sources.Add(chat.Source);
                }
        }

        if ((intent.Messages || overview) && chats.Count > 0)
        {
            sb.AppendLine().AppendLine("Recent chats (newest first):");
            foreach (var chat in chats.Take(12))
                sb.AppendLine($"- {chat.Name} ({chat.Source}){(chat.Unread > 0 ? $", {chat.Unread} unread" : "")}: last message {(chat.LastFromMe ? "from me" : "from them")} on {chat.LastAt.ToLocalTime():d MMM HH:mm}: {Clip(chat.LastText, 140)}");
            foreach (var s in chats.Take(12).Select(c => c.Source).Distinct()) if (!sources.Contains(s)) sources.Add(s);
        }

        // Search the chats for the question's own words, so "who's coming to the party?" finds the party messages.
        var words = AssistantIntent.Keywords(question);
        if (words.Count > 0)
        {
            var found = chats.SelectMany(c => c.Recent.Where(m => words.Any(w => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Select(m => (Chat: c, Msg: m)))
                .OrderByDescending(x => x.Msg.At).Take(12).ToList();
            if (found.Count > 0)
            {
                sb.AppendLine().AppendLine($"Messages that mention {string.Join(", ", words.Select(w => $"\"{w}\""))}:");
                foreach (var (chat, m) in found)
                    sb.AppendLine($"- [{m.At.ToLocalTime():d MMM HH:mm}] {chat.Name} ({chat.Source}) {(m.FromMe ? "Me" : m.Who)}: {Clip(m.Text, 200)}");
                foreach (var src in found.Select(f => f.Chat.Source).Distinct()) if (!sources.Contains(src)) sources.Add(src);
            }
        }

        if (intent.Mail || overview)
        {
            var mail = connections.Any(c => c.IntegrationKey == "gmail") ? await recentMail() : null;
            if (mail is { Count: > 0 })
            {
                sb.AppendLine().AppendLine("Recent Gmail inbox (newest first):");
                foreach (var m in mail.Take(10))
                    sb.AppendLine($"- {(m.IsUnread ? "[unread] " : "")}From {Clip(m.From, 60)} — {Clip(m.Subject, 90)}: {Clip(m.Snippet, 100)}");
                sources.Add("Gmail");
            }
            else if (intent.Mail) sb.AppendLine().AppendLine("Gmail: not connected or could not be read right now.");
            if (intent.Codes && mail is { Count: > 0 })
            {
                var codes = mail.Where(m => OtpExtractor.LooksLikeOtp(m.Subject, m.Snippet))
                    .Select(m => (Mail: m, Code: OtpExtractor.Extract(m.Subject, m.Snippet))).Where(x => x.Code is not null).Take(5).ToList();
                sb.AppendLine().AppendLine(codes.Count == 0 ? "No one-time codes were found in the recent emails." : "One-time codes in recent emails (newest first):");
                foreach (var (m, code) in codes) sb.AppendLine($"- {code} from {Clip(m.From, 50)} — {Clip(m.Subject, 70)}");
            }
        }

        if (intent.Lists || intent.Tasks || overview)
        {
            var open = await ListsModule.OpenAsync(db, userId, 60, ct);
            if (open.Count > 0)
            {
                sb.AppendLine().AppendLine("The owner's lists (open items):").AppendLine(ListsModule.Summarise(open));
                sources.Add("Lists");
            }
            else if (intent.Lists) sb.AppendLine().AppendLine("The owner's lists are empty.");
        }

        if (intent.Mail || intent.Codes)
        {
            var flagged = await db.GetCollection<GmailMessageDoc>("gmailMessages")
                .Find(d => d.UserId == userId && (d.Important || d.Urgent) && d.DateUtc >= DateTime.UtcNow.AddDays(7 * -1))
                .SortByDescending(d => d.DateUtc).Limit(8).ToListAsync(ct);
            if (flagged.Count > 0)
            {
                sb.AppendLine().AppendLine("Important or urgent emails this week:");
                foreach (var d in flagged) sb.AppendLine($"- {(d.Urgent ? "[urgent] " : "")}{(d.DateUtc is { } when ? when.ToLocalTime().ToString("d MMM HH:mm") : "")} From {Clip(d.From, 50)} — {Clip(d.Subject, 90)}");
            }
        }

        if (intent.Tasks || overview)
        {
            var connectionIds = connections.Select(c => c.Id).ToList();
            var open = await db.GetCollection<WhatsAppInsight>("whatsappInsights")
                .Find(i => connectionIds.Contains(i.ConnectionId) && !i.Done && (i.Kind == "Task" || i.Kind == "Reminder"))
                .SortByDescending(i => i.CreatedAt).Limit(15).ToListAsync(ct);
            if (open.Count > 0)
            {
                sb.AppendLine().AppendLine("Open tasks and reminders picked up from chats:");
                foreach (var i in open) sb.AppendLine($"- [{i.Kind}] {Clip(i.Text, 140)}{(i.ChatName is null ? "" : $" (chat: {i.ChatName})")}{(i.DueAt is { } due ? $", due {due.ToLocalTime():d MMM HH:mm}" : "")}");
                sources.Add("Tasks from chats");
            }
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxChars) text = text[..MaxChars].TrimEnd() + "\n…(more not shown)";
        return new AssistantContextResult(text, sources);
    }

    private static async Task<IEnumerable<ChatInfo>> WhatsAppChatsAsync(IMongoDatabase db, IntegrationConnection connection, CancellationToken ct)
    {
        var messages = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => m.ConnectionId == connection.Id && !m.Hidden && !m.Deleted).SortByDescending(m => m.Timestamp).Limit(400).ToListAsync(ct);
        var contacts = await db.GetCollection<WhatsAppContact>("whatsappContacts").Find(c => c.ConnectionId == connection.Id).ToListAsync(ct);
        var saved = new Dictionary<string, string>(); var selfChosen = new Dictionary<string, string>();
        foreach (var c in contacts)
        {
            var ids = new[] { c.ContactId, c.Lid, c.PhoneNumber }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
            if (!string.IsNullOrWhiteSpace(c.Name)) foreach (var id in ids) saved[id] = c.Name!;
            if (!string.IsNullOrWhiteSpace(c.Notify)) foreach (var id in ids) selfChosen[id] = c.Notify!;
        }
        var viewed = connection.LastViewedAt ?? DateTime.MinValue;
        return messages.GroupBy(m => m.ChatId).Select(g =>
        {
            var name = IntegrationsModule.PickChatName(g.Key, saved, selfChosen, g.Select(m => (m.FromMe, m.ChatName))) ?? (WhatsAppChat.IsGroup(g.Key) ? "WhatsApp group" : $"+{g.Key.Split('@')[0]}");
            var last = g.First();
            return new ChatInfo("WhatsApp", name, g.Count(m => !m.FromMe && m.Timestamp > viewed), last.Timestamp, last.FromMe, last.Text,
                g.Take(25).Select(m => (m.Timestamp, m.FromMe, m.FromMe ? "Me" : (WhatsAppChat.IsGroup(g.Key) ? (m.ChatName ?? "Someone") : name), m.Text)).ToList());
        });
    }

    private static async Task<IEnumerable<ChatInfo>> TelegramChatsAsync(IMongoDatabase db, IntegrationConnection connection, CancellationToken ct)
    {
        var messages = await db.GetCollection<TelegramMessage>("telegramMessages")
            .Find(m => m.ConnectionId == connection.Id && !m.Hidden && !m.Deleted).SortByDescending(m => m.Timestamp).Limit(400).ToListAsync(ct);
        var viewed = connection.LastViewedAt ?? DateTime.MinValue;
        return messages.GroupBy(m => m.ChatId).Select(g =>
        {
            var name = g.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ChatName))?.ChatName ?? g.Key;
            var last = g.First();
            return new ChatInfo("Telegram", name, g.Count(m => !m.FromMe && m.Timestamp > viewed), last.Timestamp, last.FromMe, last.Text,
                g.Take(25).Select(m => (m.Timestamp, m.FromMe, m.FromMe ? "Me" : (m.SenderName ?? name), m.Text)).ToList());
        });
    }
}
