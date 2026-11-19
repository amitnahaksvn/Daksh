using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record AssistantContextResult(string Text, IReadOnlyList<string> Sources);

/// <summary>Works out, from the words of a question, which connected apps are worth looking at. Pure, so it is tested directly.</summary>
public static class AssistantIntent
{
    public sealed record Intent(bool Messages, bool Mail, bool Tasks, bool Overview, IReadOnlyList<string> Names)
    {
        public bool AnyApp => Messages || Mail || Tasks || Overview || Names.Count > 0;
    }

    private static readonly Regex MessagesRx = new(@"\b(whats?app|telegram|messages?|msgs?|chats?|dms?|texts?|texted|replied|reply|said|say|told|unread|missed|groups?|conversation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MailRx = new(@"\b(mails?|e-?mails?|gmail|inbox|subject|invoices?|newsletters?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TasksRx = new(@"\b(tasks?|to-?dos?|reminders?|remind|due|deadlines?|overdue|pending|follow-?ups?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        return new Intent(MessagesRx.IsMatch(q), MailRx.IsMatch(q), TasksRx.IsMatch(q), OverviewRx.IsMatch(q), matched);
    }

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

        if (intent.Names.Count > 0)
        {
            foreach (var name in intent.Names)
                foreach (var chat in chats.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2))
                {
                    sb.AppendLine().AppendLine(FormatChat($"{chat.Name} ({chat.Source})", chat.Recent.Take(20)));
                    if (!sources.Contains(chat.Source)) sources.Add(chat.Source);
                }
        }

        if ((intent.Messages || intent.Overview) && chats.Count > 0)
        {
            sb.AppendLine().AppendLine("Recent chats (newest first):");
            foreach (var chat in chats.Take(12))
                sb.AppendLine($"- {chat.Name} ({chat.Source}){(chat.Unread > 0 ? $", {chat.Unread} unread" : "")}: last message {(chat.LastFromMe ? "from me" : "from them")} on {chat.LastAt.ToLocalTime():d MMM HH:mm}: {Clip(chat.LastText, 140)}");
            foreach (var s in chats.Take(12).Select(c => c.Source).Distinct()) if (!sources.Contains(s)) sources.Add(s);
        }

        if (intent.Mail || intent.Overview)
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
        }

        if (intent.Tasks || intent.Overview)
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
