using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public enum GateVerdict
{
    /// <summary>Say nothing: media, reels, emoji, plain acknowledgements, group chatter that wasn't meant for the assistant.</summary>
    Silent,
    /// <summary>A greeting or pleasantry — safe for the AI to answer.</summary>
    AiOk,
    /// <summary>Needs a decision, a commitment or information only the owner has. Don't answer; say the boss will get back.</summary>
    Boss,
    /// <summary>Same, but too vague to pass on as it is — ask the sender for more detail first.</summary>
    BossAskMore,
}

public sealed record GateDecision(GateVerdict Verdict, string Reason);

/// <summary>
/// The rule gate that runs BEFORE any AI reply on WhatsApp and Telegram. The AI is only ever allowed to answer simple social messages
/// (hi, thanks, how are you). Anything that asks for a decision, a commitment, money, time, a document or any information — and anything
/// the gate does not positively recognise as harmless — is held for the account owner ("my boss"), and the sender gets a holding reply.
/// Reels, images, videos, voice notes, stickers and bare emoji never get a reply. Pure and deterministic, so it can be tested exhaustively.
/// </summary>
public static class ReplyGate
{
    public const string DefaultBoss = "my boss";

    // ── Silent: media and shares ────────────────────────────────────────────────────────────────
    private static readonly string[] MediaPlaceholders = ["📷", "🎥", "🎤", "📄", "📍", "👤", "📞", "📹", "🖼", "🎞", "🎬", "🎧", "📎", "sticker", "gif", "photo", "video", "voice message", "audio", "document", "image", "contact"];
    private static readonly Regex Url = new(@"(https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaSite = new(
        @"(instagram\.com/(reel|reels|p|tv|stories)/|instagr\.am/|facebook\.com/(reel|watch|share/(r|v))|fb\.watch|fb\.me/|tiktok\.com|youtube\.com/(shorts|watch)|youtu\.be/|snapchat\.com|vimeo\.com|x\.com/\S+/status|twitter\.com/\S+/status|pinterest\.|giphy\.com|tenor\.com|sharechat\.com|moj|josh)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaWord = new(@"\b(reels?|images?|img|photos?|pics?|pictures?|videos?|screenshots?|stickers?|gifs?|memes?|status)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JustReal = new(@"^\s*reals?\s*[.!]*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase); // "real" as a typo for "reel"

    // ── Casual: what the AI may answer ──────────────────────────────────────────────────────────
    private static readonly Regex Greeting = new(
        @"^(hi+|hii+|hello+|helo+|hey+|heya|hola|yo|namaste|namaskar|namaskaram|ram ram|jai shri krishna|jai shree krishna|jai hind|radhe radhe|salam|salaam|assalamu alaikum|sat sri akal|good (morning|afternoon|evening|night)|gm|gn|morning|evening|hi there|hello there|hey there)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Pleasantry = new(
        @"^(how are (you|u)|how r (you|u)|how do you do|how'?s it going|how have you been|hope you('?re| are) (doing )?(well|fine|good)|what'?s up|whats up|wassup|sup|kaise ho|kaise hain|kaisa hai|kya haal( hai)?|kya hal|kem cho|how is everything|all good|everything fine)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Thanks = new(@"^(thanks|thank you|thankyou|thx|ty|many thanks|thanks a lot|thank you so much|thanks so much|dhanyavad|dhanyavaad|shukriya|shukria)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Wishes = new(
        @"^(happy (birthday|diwali|holi|new year|eid|christmas|anniversary|navratri|dussehra|pongal|onam|raksha bandhan|ganesh chaturthi|makar sankranti|baisakhi|gudi padwa)|many many happy returns( of the day)?|hbd|congrats|congratulations|best wishes|all the best|wish you (a )?(happy|great|good).*|have a (nice|great|good) (day|evening|weekend|night)|good luck)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Goodbye = new(@"^(bye+|good ?bye|see you( later| soon)?|see ya|take care|ttyl|talk later|cya|good night|gn|tc)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Messages that need no answer at all ("ok", "👍", "noted").</summary>
    private static readonly Regex Acknowledgement = new(
        @"^(ok+a?y?|okk+|k+|kk|fine|sure|yes|yep|yeah|yup|no|nope|nah|cool|nice|great|good|awesome|amazing|super|alright|noted|got it|understood|done|hmm+|hm+|lol|lmao|haha+|hehe+|welcome|you('?re| are) welcome|np|no problem|no worries|theek hai|thik hai|accha|achha|acha|haan|haa|ha|nahi|ji|ji haan|sahi|right|true|exactly|correct)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Forms of address that never change what a message means — dropped wherever they appear ("hello bhai kaise ho" → "hello kaise ho").</summary>
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "sir", "sirji", "madam", "mam", "ma'am", "maam", "bhai", "bhaiya", "bro", "brother", "sis", "dear", "boss", "ji", "sahab", "sahib", "saheb", "didi", "anna", "uncle", "aunty", "auntie",
    };
    /// <summary>Only dropped at the END of a clause ("hi everyone", "hello friend"), because elsewhere they are ordinary words ("all the best", "there you go").</summary>
    private static readonly HashSet<string> TrailingAddress = new(StringComparer.OrdinalIgnoreCase) { "friend", "buddy", "there", "everyone", "all", "guys" };

    // ── Boss: anything asking for a decision or information ─────────────────────────────────────
    private static readonly Regex NeedsBoss = new(
        string.Join('|',
        [
            @"\b(can|could|would|will|shall|may|should|do|did|are|is|were|was) (you|u|we|i|he|she|they)\b",
            @"\b(please|pls|plz|kindly)\b",
            @"\b(need|needs|needed|want|wants|require|required|requirement|looking for|looking to|interested)\b",
            @"\b(urgent|urgently|asap|immediately|emergency|important|priority|critical)\b",
            @"\b(pay|paid|payment|payments|transfer|send|share|forward|give|provide|submit|upload|email|mail|deposit|refund|settle|clear)\b",
            @"\b(meet|meeting|call|ring|phone|visit|come|reach|join|attend|appointment|schedule|reschedule|postpone|cancel|available|availability|free|busy|slot|tomorrow|today|tonight|tonite|morning|evening|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
            @"\b(price|prices|cost|quote|quotation|rate|rates|discount|fee|fees|amount|invoice|bill|salary|loan|money|rs\.?|inr|usd|upi|bank|account|cheque|check|advance|balance|budget|offer|deal|negotiate)\b",
            @"[₹$€£]",
            @"\b(confirm|confirmation|approve|approval|permission|allow|sign|decision|decide|agree|accept|reject|order|book|booking|deliver|delivery|ship|dispatch|status|update|deadline|due|pending|follow ?up|response|respond|reply)\b",
            @"\b(where|when|why|how much|how many|what time|which|who|whom|whose|how long|how soon)\b",
            @"\b(help|problem|issue|complaint|wrong|error|not working|failed|failure|stuck|broken|trouble|cannot|can't|cant|unable)\b",
            @"\b(kab|kahan|kahaan|kyun|kyu|kitna|kitne|kitni|paisa|paise|bhej(o|na|dena|do)?|bata(o|na|dena)?|batao|chahiye|chahie|zaroori|jaruri|jaldi|call karo|baat karo|mil(o|na|na hai)|aao|aana|aaja|kaam|madad|bula(o|na)|dikhao|de do|dena)\b",
            @"\?",
        ]),
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"Call me", "urgent", "need help": a request with no subject, which the boss can't act on without asking back.</summary>
    private static readonly Regex Vague = new(
        @"^(call( me)?( back)?|please call( me)?|pls call( me)?|call karo|ring me|missed call|urgent|very urgent|important|help|help me|need help|need you|need your help|talk|can we talk|let'?s talk|are you there|you there|busy|are you busy|free|are you free|reply|respond|please reply|pls reply|come|where|where are you|asap|baat karni hai|baat karo|zaroori kaam|urgent hai|jaldi call karo|\?+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="inConversation">True when this chat has been busy in the last few minutes. A short fragment is then part of that
    /// conversation, so it goes to the owner as it is instead of the sender being asked to explain it.</param>
    public static GateDecision Evaluate(string? text, bool isGroup, bool inConversation = false)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return new(GateVerdict.Silent, "Empty message.");

        // 1) Media and shares are never answered.
        var lower = raw.ToLowerInvariant();
        if (MediaPlaceholders.Any(p => lower.StartsWith(p, StringComparison.Ordinal) && (p.Length <= 2 || lower.Length == p.Length || !char.IsLetter(lower[p.Length]))) && !raw.Contains('?') && raw.Length < 40)
            return new(GateVerdict.Silent, "A photo, video, voice note, sticker or other media — no reply.");
        if (raw.StartsWith("📞") || raw.StartsWith("📹")) return new(GateVerdict.Silent, "A call notice.");
        var urls = Url.Matches(raw).Select(m => m.Value).ToList();
        var withoutUrls = Url.Replace(raw, " ").Trim();
        if (urls.Any(u => MediaSite.IsMatch(u))) return new(GateVerdict.Silent, "A shared reel or video link — no reply.");
        if (JustReal.IsMatch(raw)) return new(GateVerdict.Silent, "A reel share — no reply.");
        if (urls.Count > 0 && withoutUrls.Length == 0) return isGroup ? new(GateVerdict.Silent, "A link in a group.") : new(GateVerdict.Boss, "A link was shared; the boss should look at it.");

        // A bare "?" / "???" means "why no reply?" — it needs the boss, but there is nothing to pass on yet.
        if (Regex.IsMatch(raw, @"^\s*\?+\s*$")) return isGroup ? new(GateVerdict.Silent, "A bare question mark in a group.") : inConversation ? new(GateVerdict.Boss, "A bare question mark in the middle of a conversation.") : new(GateVerdict.BossAskMore, "A bare question mark — ask what they need.");

        // 2) Symbols and emoji only.
        if (!raw.Any(c => char.IsLetterOrDigit(c))) return new(GateVerdict.Silent, "Emoji or symbols only.");

        // 3) Mentions of a reel / image / video that aren't asking for anything: they're sharing one.
        if (MediaWord.IsMatch(raw) && !NeedsBoss.IsMatch(MediaWord.Replace(raw, " "))) return new(GateVerdict.Silent, "Talking about a reel or image — no reply.");

        // 4) Clause by clause: greetings/pleasantries are casual, "ok" needs nothing, everything else is a real message.
        var clauses = Regex.Split(withoutUrls, @"[\n,.;!]+|\s+(?:and|aur|&)\s+")
            .Select(Clean).Where(c => c.Length > 0).ToList();
        var casual = 0; var other = new List<string>();
        foreach (var clause in clauses)
        {
            if (IsCasual(clause)) casual++;
            else if (Acknowledgement.IsMatch(clause) || !clause.Any(char.IsLetterOrDigit)) continue;
            else other.Add(clause);
        }
        if (other.Count == 0)
            return casual > 0 ? new(GateVerdict.AiOk, "A greeting or pleasantry.") : new(GateVerdict.Silent, "An acknowledgement that needs no answer.");

        // 5) A real message. Never let the AI answer it; hold it for the boss. In a group that wasn't addressed to the assistant, stay quiet.
        if (isGroup) return new(GateVerdict.Silent, "A group message that needs a decision — the boss will see it; no reply in the group.");
        var body = string.Join(" ", other);
        var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (inConversation && words <= 4)
            return new(GateVerdict.Boss, "A short message that is part of an ongoing conversation — the owner should read it in context.");
        if (words <= 2 || Vague.IsMatch(Clean(body)))
            return new(GateVerdict.BossAskMore, "A request with no detail — ask for more information before passing it on.");
        return new(GateVerdict.Boss, NeedsBoss.IsMatch(body) ? "Asks for a decision, commitment or information." : "Not a simple greeting — only the boss can answer this.");
    }

    private static bool IsCasual(string clause)
    {
        if (Greeting.IsMatch(clause) || Pleasantry.IsMatch(clause) || Thanks.IsMatch(clause) || Wishes.IsMatch(clause) || Goodbye.IsMatch(clause)) return true;
        // A greeting run into a pleasantry with no punctuation: "hello kaise ho", "good morning how are you", "hi thanks".
        var tokens = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var k = 1; k <= Math.Min(3, tokens.Length - 1); k++)
        {
            var head = string.Join(' ', tokens.Take(k));
            var rest = string.Join(' ', tokens.Skip(k));
            if (Greeting.IsMatch(head) && (Pleasantry.IsMatch(rest) || Thanks.IsMatch(rest) || Wishes.IsMatch(rest) || Greeting.IsMatch(rest))) return true;
        }
        return false;
    }

    /// <summary>Lower-cases, drops "?" and other punctuation, and removes honorifics ("hi sir" → "hi").</summary>
    private static string Clean(string clause)
    {
        var s = Regex.Replace(clause.ToLowerInvariant().Replace('’', '\''), @"[^\p{L}\p{N}\s'?&]", " ");
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // A leading or trailing run of honorifics doesn't change what the message is ("hello sir", "bhai kaise ho").
        while (tokens.Count > 1 && (Honorifics.Contains(tokens[^1]) || TrailingAddress.Contains(tokens[^1]))) tokens.RemoveAt(tokens.Count - 1);
        for (var i = tokens.Count - 1; i >= 0 && tokens.Count > 1; i--)
            if (Honorifics.Contains(tokens[i])) tokens.RemoveAt(i);
        var joined = string.Join(' ', tokens).Replace("?", "").Trim();
        return joined.Length == 0 && s.Contains('?') ? "?" : joined;
    }

    // ── The holding replies ─────────────────────────────────────────────────────────────────────
    public static string NormaliseBoss(string? boss) => string.IsNullOrWhiteSpace(boss) ? DefaultBoss : boss.Trim().Length > 60 ? boss.Trim()[..60] : boss.Trim();

    private static string Pick(string seed, string[] options) => options[(int)((uint)StableHash(seed) % (uint)options.Length)];
    private static int StableHash(string s) { unchecked { var h = 17; foreach (var c in s) h = h * 31 + c; return h; } }

    /// <summary>What the sender is told when the message needs the boss. Written as an assistant, never as the boss.</summary>
    public static string HoldingReply(GateVerdict verdict, string? boss, string seed)
    {
        var b = NormaliseBoss(boss);
        return verdict == GateVerdict.BossAskMore
            ? Pick(seed,
            [
                $"Thanks for reaching out! Could you share a few more details (what it's about and when you need it)? I'll pass it on to {b}, and {b} will get back to you once they see your message.",
                $"Hi! So I can pass this on properly, could you tell me a bit more about what you need? I'll let {b} know, and {b} will get back to you.",
            ])
            : Pick(seed,
            [
                $"Thanks for your message! I'll let {b} know, and {b} will get back to you as soon as they see it.",
                $"Hi! Please wait until {b} sees your message — I'll let {b} know and get back to you.",
                $"Got it, thank you. I can't decide this myself, so I'll check with {b} and get back to you.",
            ]);
    }

    /// <summary>What an AI answer looks like when the model itself decided the message is beyond small talk (the prompt tells it to say NEED_BOSS).</summary>
    public static bool ModelSaidNeedBoss(string? reply) => reply is not null && Regex.IsMatch(reply, @"\bNEED_BOSS\b", RegexOptions.IgnoreCase);
}

public enum GateAction { Silent, SendHolding, UseAi }
public sealed record GateOutcome(GateAction Action, string? Text, string Reason);

/// <summary>Applies the gate for one incoming message: decides the outcome, creates the boss's to-do, and rate-limits holding replies.</summary>
public static class ReplyGateFlow
{
    public static async Task<GateOutcome> ResolveAsync(
        IMongoDatabase db, WhatsAppSettings settings, string connectionId, string chatId, string? chatName, string text, bool isGroup,
        Func<DateTime, Task<bool>> ownerRepliedSince, DateTime nowUtc, GateVerdict? forced = null, CancellationToken ct = default, bool inConversation = false)
    {
        if (!settings.ReplyGateEnabled && forced is null) return new(GateAction.UseAi, null, "The reply gate is turned off.");

        var decision = forced is { } f ? new GateDecision(f, "The AI said this needs the boss.") : ReplyGate.Evaluate(text, isGroup, inConversation);
        switch (decision.Verdict)
        {
            case GateVerdict.Silent: return new(GateAction.Silent, null, decision.Reason);
            case GateVerdict.AiOk: return new(GateAction.UseAi, null, decision.Reason);
        }

        // The boss gets a to-do either way, so nothing that needs a decision is lost even when we stay quiet.
        var boss = ReplyGate.NormaliseBoss(settings.BossReference);
        await AddBossTaskAsync(db, connectionId, chatId, chatName, text, ct);

        var state = db.GetCollection<WhatsAppAutoReplyState>("whatsappAutoReplyState");
        var existing = await state.Find(s => s.ConnectionId == connectionId && s.ChatId == chatId).FirstOrDefaultAsync(ct);
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, settings.GateHoldingCooldownMinutes));
        if (existing?.LastHoldingAt is { } last && cooldown > TimeSpan.Zero && nowUtc - last < cooldown && !await ownerRepliedSince(last))
            return new(GateAction.Silent, null, "A holding reply was already sent to this chat and the boss hasn't answered yet.");

        await state.UpdateOneAsync(s => s.ConnectionId == connectionId && s.ChatId == chatId,
            Builders<WhatsAppAutoReplyState>.Update.Set(s => s.LastHoldingAt, nowUtc).SetOnInsert(s => s.ConnectionId, connectionId).SetOnInsert(s => s.ChatId, chatId).SetOnInsert(s => s.LastRepliedAt, DateTime.MinValue),
            new UpdateOptions { IsUpsert = true }, ct);
        return new(GateAction.SendHolding, ReplyGate.HoldingReply(decision.Verdict, boss, chatId + text), decision.Reason);
    }

    private static async Task AddBossTaskAsync(IMongoDatabase db, string connectionId, string chatId, string? chatName, string text, CancellationToken ct)
    {
        var trimmed = text.Length <= 240 ? text : text[..240] + "…";
        var who = string.IsNullOrWhiteSpace(chatName) ? chatId.Split('@')[0] : chatName;
        var insights = db.GetCollection<WhatsAppInsight>("whatsappInsights");
        var recent = DateTime.UtcNow.AddMinutes(-30);
        var label = $"Needs your reply — {who}: {trimmed}";
        if (await insights.Find(i => i.ConnectionId == connectionId && i.ChatId == chatId && i.Text == label && i.CreatedAt >= recent).AnyAsync(ct)) return;
        await WhatsAppAutomationModule.AddInsightAsync(db, new WhatsAppInsight
        {
            ConnectionId = connectionId, ChatId = chatId, ChatName = chatName, Kind = "Task", Text = label, Value = "needs-boss", CreatedAt = DateTime.UtcNow,
        }, ct);
    }
}
