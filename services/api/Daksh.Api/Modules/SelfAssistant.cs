using Daksh.Ai;
using Daksh.Platform.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>
/// Ask Daksha from your OWN chat ("Message yourself" on WhatsApp, "Saved Messages" on Telegram) and get the answer there:
/// "what's pending?", "any important email?", "any OTP?", "what's on my Amazon list?", "what did Ravi say about the party?".
/// Only the owner's own chat is ever read this way. Uses the same assistant (and the same privacy consent) as the app.
/// </summary>
public static class SelfAssistant
{
    private static readonly Regex Addressed = new(@"^\s*(hey |hi |ok |okay )?(daksha|daksh)\b[\s,:\-]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionStart = new(
        @"^\s*(what|whats|what's|who|whom|when|where|which|why|how|any|anything|is|are|do|does|did|have|has|can|could|show|tell|give|list|check|find|search|read|summari[sz]e|remind|pending|status|latest|last|new)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when a message to yourself reads like a question for Daksha rather than a note to yourself.</summary>
    public static bool LooksLikeQuestion(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 3 || t.Length > 500 || t.StartsWith(SelfCommands.ReplyMarker, StringComparison.Ordinal)) return false;
        if (SelfCommands.Parse(t).Kind != SelfCommandKind.None) return false;
        if (Regex.IsMatch(t, @"^\s*(https?://|www\.)\S+\s*$", RegexOptions.IgnoreCase)) return false; // a pasted link is a note
        return Addressed.IsMatch(t) || t.EndsWith('?') || QuestionStart.IsMatch(t);
    }

    /// <summary>True when a message in your own chat is worth looking at: not our own reply, not empty, not just a pasted link.</summary>
    public static bool IsCandidate(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 2 || t.Length > 1000 || t.StartsWith(SelfCommands.ReplyMarker, StringComparison.Ordinal)) return false;
        return !Regex.IsMatch(t, @"^\s*(https?://|www\.)\S+\s*$", RegexOptions.IgnoreCase);
    }

    private const string IntentPrompt = """
        You watch the private "message yourself" chat of a user. They write there both notes to themselves and requests to their personal AI assistant, and never say the assistant's name.
        Decide whether the message is meant FOR the assistant (a request to do something, or a question about their lists, busy status, email, chats, OTPs, reminders, calendar, tasks) or is just a note to self (a thought, a reminder text, a pasted snippet, an address, a draft, small talk).
        The user is the account owner and what they ask is an order. Polite or indirect phrasing ("can you add...", "please...", "could you...", "I want you to...") is a request for the assistant, never a note.
        Choose "ignore" only when the message plainly is not asking the assistant for anything.
        Reply with ONE line of JSON and nothing else:
        {"intent":"ignore|question|busy|free|add|remove|show|event|calendar|send","list":null,"text":null,"minutes":null,"until":null,"note":null,"title":null,"start":null,"location":null,"from":null,"to":null}
        - "question": they want information or help; the assistant will answer it.
        - "busy": they want auto-replies on. Set "minutes" (number) or "until" ("HH:mm", 24h), and "note" for a reason if given.
        - "free": they want auto-replies off.
        - "add": add "text" to the list named in "list". Use one of the user's existing lists when the wording matches it; set "list" to null whenever the user did not name a list (do not guess one).
        - "remove": remove "text" (optionally from "list").
        - "show": show their lists.
        - "event": add a calendar event. Set "title", "start" (local time, "yyyy-MM-ddTHH:mm", worked out from the current time given below), "minutes" (length, 60 if not said) and "location" if given.
        - "calendar": they ask what is on their calendar / schedule / agenda. Set "from" and "to" as local dates "yyyy-MM-dd" (the same day for "today"; "to" is inclusive).
        - "send": they want a message sent to someone on a messaging app. Set "to" (the person's name or phone number) and "text" (the message, written the way the owner would say it, in the same language they used; keep their own words when they dictate them).
        """;

    /// <summary>
    /// The one entry point for a message in the owner's own chat: keyword commands first (fast, free), otherwise the AI decides whether the
    /// message is meant for the assistant at all. Returns the reply to send (already marked as ours), or null to stay silent.
    /// </summary>
    public static async Task<string?> HandleAsync(IMongoDatabase db, IAssistantOrchestrator orchestrator, ISecretProtector protector, IConfiguration config,
        IHttpClientFactory clients, string userId, string text, ILogger log, CancellationToken ct, ISelfMessenger? messenger = null)
    {
        if (!IsCandidate(text)) return null;
        var cmd = SelfCommands.Parse(text);
        var pending = TakePending(userId);
        if (cmd.Kind == SelfCommandKind.None && pending is not null && IsListAnswer(text))
        {
            if (Regex.IsMatch(text, @"^\s*(cancel|never ?mind|forget it|skip|no|nope|stop)\s*[.!]*\s*$", RegexOptions.IgnoreCase)) return SelfCommands.ReplyMarker + "👍 Okay, I won't add it.";
            var name = ListsModule.CleanName(Regex.Replace(text.Trim(), @"^(to |in |into |on |the |my )+|(\s+list)\s*$", "", RegexOptions.IgnoreCase));
            return await SelfCommands.ExecuteAsync(db, userId, new SelfCommand(SelfCommandKind.AddItem, name, pending), DateTime.UtcNow, ct);
        }
        if (cmd.Kind != SelfCommandKind.None) return await SelfCommands.ExecuteAsync(db, userId, cmd, DateTime.UtcNow, ct);

        var privacy = await PrivacyStore.GetAsync(db, userId, ct);
        if (!privacy.AllowProviderDataSharing)
        {
            // Without consent nothing can be sent to the AI to judge the message, so only the obvious questions are acted on.
            return LooksLikeQuestion(text) ? await AnswerAsync(db, orchestrator, protector, config, clients, userId, text, log, ct) : null;
        }

        SelfIntent intent;
        try
        {
            var lists = (await ListsModule.OpenAsync(db, userId, 200, ct)).Select(i => i.List).Distinct().Take(20).ToList();
            var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var known = (lists.Count == 0 ? "The user has no lists yet." : "The user's existing lists: " + string.Join(", ", lists) + ".")
                + $"\nCurrent local time: {now:dddd yyyy-MM-dd HH:mm} ({tz.Id}).";
            var result = await orchestrator.RespondAsync(new AssistantRequest(new UserAiContext(userId, false, AllowProviderDataSharing: true), text, null, known, IntentPrompt), ct);
            intent = ParseIntent(result.IsSimulated ? null : result.Text);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the intent of a message in the owner's own chat for user {UserId}; leaving it alone", userId);
            return null;
        }

        log.LogInformation("Own-chat message read as {Intent} for user {UserId}", intent.Name, userId);
        switch (intent.Name)
        {
            case "event":
            {
                if (intent.Arg("title") is not { } title || !DateTime.TryParse(intent.Arg("start"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
                    return SelfCommands.ReplyMarker + "Which day and time should I put it on your calendar? Tell me like \"dentist tomorrow 5pm\".";
                var minutes = int.TryParse(intent.Arg("minutes"), out var mm) ? mm : 60;
                return SelfCommands.ReplyMarker + await CalendarAssistant.CreateAsync(db, protector, config, clients, userId, title, start, minutes, intent.Arg("location"), ct);
            }
            case "calendar":
            {
                var tz = await CalendarAssistant.TimezoneAsync(db, userId, ct);
                var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
                var from = DateTime.TryParse(intent.Arg("from"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f) ? f.Date : today;
                var to = DateTime.TryParse(intent.Arg("to"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t.Date : from;
                if (to < from || to - from > TimeSpan.FromDays(31)) to = from;
                return SelfCommands.ReplyMarker + await CalendarAssistant.AgendaAsync(db, protector, config, clients, userId, from, to.AddDays(1), ct);
            }
            case "send":
            {
                if (messenger is null) return SelfCommands.ReplyMarker + "ℹ️ I can't send messages from this chat yet.";
                if (intent.Arg("to") is not { } to || intent.Arg("text") is not { } body) return SelfCommands.ReplyMarker + "Who should I message, and what should I say?";
                return SelfCommands.ReplyMarker + await SelfSend.RunAsync(messenger, to, body, ct);
            }
        }
        if (intent.Command is { Kind: SelfCommandKind.AddItem, List: null } add)
        {
            // No list named: ask instead of guessing, and treat the user's next short message as the answer.
            SetPending(userId, add.Text!);
            var names = (await ListsModule.OpenAsync(db, userId, 200, ct)).Select(i => i.List).Distinct().Take(8).ToList();
            var options = names.Count == 0 ? $"{ListsModule.DefaultShopping} or {ListsModule.DefaultTodo}" : string.Join(", ", names);
            return SelfCommands.ReplyMarker + $"Which list should I add \"{add.Text}\" to? ({options}, or any new name). Reply with the list name, or \"cancel\".";
        }
        if (intent.Command is { } c) return await SelfCommands.ExecuteAsync(db, userId, c, DateTime.UtcNow, ct);
        if (intent.Name == "question") return await AnswerAsync(db, orchestrator, protector, config, clients, userId, text, log, ct);
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Text, DateTime At)> PendingAdds = new();
    private static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(10);

    private static void SetPending(string userId, string item) => PendingAdds[userId] = (item, DateTime.UtcNow);

    /// <summary>The item the assistant is waiting to file under a list, if it asked in the last few minutes. Taking it clears it.</summary>
    private static string? TakePending(string userId) =>
        PendingAdds.TryRemove(userId, out var p) && DateTime.UtcNow - p.At <= PendingFor ? p.Text : null;

    /// <summary>A short, question-free reply reads as the answer to "which list?"; anything longer is a new message.</summary>
    private static bool IsListAnswer(string text) { var t = text.Trim(); return t.Length <= 40 && !t.Contains('?'); }

        public sealed record SelfIntent(string Name, SelfCommand? Command = null, IReadOnlyDictionary<string, string>? Args = null)
    {
        public string? Arg(string key) => Args is not null && Args.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    /// <summary>Reads the model's one-line JSON. Anything malformed or unknown is "ignore".</summary>
    public static SelfIntent ParseIntent(string? raw)
    {
        var ignore = new SelfIntent("ignore");
        if (string.IsNullOrWhiteSpace(raw)) return ignore;
        var a = raw.IndexOf('{'); var b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return ignore;
        try
        {
            using var doc = JsonDocument.Parse(raw[a..(b + 1)]);
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            var name = (Str("intent") ?? "ignore").ToLowerInvariant();
            var item = Str("text"); var list = Str("list");
            var args = new Dictionary<string, string>();
            foreach (var prop in r.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString())) args[prop.Name] = prop.Value.GetString()!.Trim();
                else if (prop.Value.ValueKind == JsonValueKind.Number) args[prop.Name] = prop.Value.GetRawText();
            switch (name)
            {
                case "event": case "calendar": case "send": return new(name, null, args);
                case "question": return new("question");
                case "free": return new("free", new SelfCommand(SelfCommandKind.Free));
                case "show": return new("show", new SelfCommand(SelfCommandKind.ShowLists));
                case "add" when item is not null:
                    return new("add", new SelfCommand(SelfCommandKind.AddItem, list is null ? null : ListsModule.CleanName(list), item));
                case "remove" when item is not null:
                    return new("remove", new SelfCommand(SelfCommandKind.RemoveItem, list is null ? null : ListsModule.CleanName(list), item));
                case "busy":
                {
                    var note = Str("note");
                    if (r.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number && m.GetDouble() is > 0 and <= 24 * 60)
                        return new("busy", new SelfCommand(SelfCommandKind.Busy, Duration: TimeSpan.FromMinutes(m.GetDouble()), Note: note));
                    if (Str("until") is { } until && TimeSpan.TryParse(until, out var clock) && clock >= TimeSpan.Zero && clock < TimeSpan.FromHours(24))
                        return new("busy", new SelfCommand(SelfCommandKind.Busy, UntilClock: $"{clock.Hours:00}:{clock.Minutes:00}", Note: note));
                    return new("busy", new SelfCommand(SelfCommandKind.Busy, Duration: TimeSpan.FromHours(2), Note: note));
                }
            }
        }
        catch (JsonException) { }
        return ignore;
    }

    public static string StripAddress(string text) => Addressed.Replace(text.Trim(), "").Trim();

    /// <summary>Answers a question for the account owner. Returns the message to send back (already marked as ours), or null if it should not be answered.</summary>
    public static async Task<string?> AnswerAsync(IMongoDatabase db, IAssistantOrchestrator orchestrator, ISecretProtector protector, IConfiguration config,
        IHttpClientFactory clients, string userId, string text, ILogger log, CancellationToken ct)
    {
        var question = StripAddress(text);
        if (question.Length < 2) return null;
        string Reply(string s) => SelfCommands.ReplyMarker + (s.Length > 3500 ? s[..3500].TrimEnd() + "…" : s);
        try
        {
            var privacy = await PrivacyStore.GetAsync(db, userId, ct);
            if (!privacy.AllowProviderDataSharing)
                return Reply("ℹ️ To answer, I need your permission to send your question and the app data it uses to the AI provider. Allow it under Privacy in the Daksha app, then ask again.");

            AssistantContextResult context;
            try
            {
                context = await AssistantContextBuilder.BuildAsync(db, userId, question,
                    accountId => GmailIntegrationModule.RecentForAssistantAsync(userId, accountId, "in:inbox newer_than:7d", 10, db, protector, config, clients, ct), ct,
                    (accountId, messageId) => GmailIntegrationModule.ReadBodyForAssistantAsync(userId, accountId, messageId, db, protector, config, clients, ct));
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not gather app data for a chat question; answering without it"); context = new AssistantContextResult("", []); }

            var chatStyle = "The person writing is the owner of this account and is giving you an instruction; do what they ask as far as your abilities and the app data allow. Never refuse or say you are not allowed or cannot 'from this chat'; if something truly cannot be done, say plainly what you can do instead (add or remove list items, set busy/free, answer questions about their data). Answer for a chat message: short, plain text, no markdown headings. Use short lines or a dash list when listing several things.";
            var result = await orchestrator.RespondAsync(new AssistantRequest(new UserAiContext(userId, privacy.MemoryEnabled, AllowProviderDataSharing: true), question, null, chatStyle + "\n" + context.Text), ct);
            if (result.IsSimulated || string.IsNullOrWhiteSpace(result.Text)) return Reply("ℹ️ I couldn't get an answer from the AI right now. Please try again in a minute.");
            return Reply(result.Text.Trim());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Chat question failed for user {UserId}", userId);
            return Reply("ℹ️ " + AssistantModule.DescribeAiFailure(ex).Title);
        }
    }
}
