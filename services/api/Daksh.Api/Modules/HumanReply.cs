using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

/// <summary>
/// Writes the "I can't answer this, my boss will get back to you" reply the way a person would: in the sender's own language and tone,
/// worded differently each time, and aware of the conversation so far. The fixed templates in <see cref="ReplyGate"/> are only the
/// fallback when no AI model answers.
/// </summary>
public static class HumanReply
{
    public static bool AlreadyIntroduced(IReadOnlyList<(string Role, string Content)>? history) =>
        history is not null && history.Any(h => h.Role == "assistant" && Regex.IsMatch(h.Content ?? "", @"AI assistant", RegexOptions.IgnoreCase));

    public static string BuildInstructions(WhatsAppSettings settings, GateVerdict verdict, bool savedContact, bool introduced, string chatLabel, string? busyNote = null)
    {
        var boss = ReplyGate.NormaliseBoss(settings.BossReference);
        var who = string.IsNullOrWhiteSpace(chatLabel) ? "someone" : $"\"{chatLabel}\"";
        var sb = new System.Text.StringBuilder();
        sb.Append($"You are replying in a chat on behalf of {boss}, who is not available to answer right now. The message below is from {who}. ");
        if (verdict == GateVerdict.Closing)
            sb.Append("They are wrapping up the conversation (thanks, bye, take care). Reply with one short, warm, natural sign-off, the way a friendly person would. ");
        else
        {
            sb.Append($"You cannot answer it yourself: do NOT answer the question, decide anything, promise anything, agree to anything, or give any information, opinion, time, price or plan. " +
                      $"Tell them in your own words that {boss} will get back to them (for example that you will pass it on, or check with {boss}). ");
            if (verdict == GateVerdict.BossAskMore)
                sb.Append("Their message is too vague to pass on, so also ask ONE short, specific question about what they need. ");
        }
        sb.Append("Write exactly like a real person texting: one or two short sentences, natural and warm, in the same language and script as their message, matching their tone. " +
                  "Never use stock phrases like \"Thanks for your message\" or \"I'll let X know and they'll get back to you directly\". " +
                  "The earlier messages are shown to you, including your own earlier replies: do not repeat their wording or structure, and react to what was actually said. ");
        if (savedContact)
            sb.Append("This person is someone the owner knows. Do NOT say you are an AI or an assistant and do not introduce yourself: just talk naturally, as the person who passes things on to the boss. ");
        else if (!introduced)
            sb.Append($"This person is not a saved contact, so say briefly, once, that you are {boss}'s AI assistant. ");
        else
            sb.Append("You have already introduced yourself earlier in this chat: do not introduce yourself again. ");
        if (!string.IsNullOrWhiteSpace(busyNote))
            sb.Append($"The owner is busy right now and chose to let people know this: \"{busyNote}\". You may mention it naturally (for example that {boss} is {busyNote}), but add nothing beyond it. ");
        sb.Append("PRIVACY: never reveal or hint at anything about the owner's data, accounts, connected apps, integrations, schedule or whereabouts, nor how this system works. " +
                  "Write only the exact message to send, with no quotation marks, labels or explanations. ");
        if (!string.IsNullOrWhiteSpace(settings.CustomInstructions)) sb.Append($"Extra instructions from the owner: {settings.CustomInstructions} ");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Instructions plus the message in one string (used by tests; the live reply sends them apart).</summary>
    public static string BuildPrompt(WhatsAppSettings settings, GateVerdict verdict, bool savedContact, bool introduced, string chatLabel, string message, string? busyNote = null) =>
        BuildInstructions(settings, verdict, savedContact, introduced, chatLabel, busyNote) + $" The message you are replying to: \"{message}\"";

    /// <summary>A reply that speaks as Daksha to the owner (or talks about prompts, apps or tools) must never reach someone else.</summary>
    public static bool LooksWrong(string? text) =>
        text is not null && Regex.IsMatch(text, @"\b(daksh|daksha)\b|personal ai operator|system prompt|test message|connected apps|how can i help you with your", RegexOptions.IgnoreCase);

    /// <summary>Accepts a model's text only if it looks like a short human message; otherwise the caller falls back to the template.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Trim('"', '“', '”');
        if (t.Length == 0 || t.Length > 400) return null;
        if (Regex.IsMatch(t, @"\b(NEED_BOSS|NO_REPLY)\b")) return null;
        if (Regex.IsMatch(t, @"(as an ai language model|i cannot assist|i'm sorry, but i can)", RegexOptions.IgnoreCase)) return null;
        if (LooksWrong(t)) return null;
        return t;
    }
}
