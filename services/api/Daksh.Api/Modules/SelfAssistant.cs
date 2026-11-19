using Daksh.Ai;
using Daksh.Platform.Security;
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

            var chatStyle = "Answer for a chat message: short, plain text, no markdown headings. Use short lines or a dash list when listing several things.";
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
