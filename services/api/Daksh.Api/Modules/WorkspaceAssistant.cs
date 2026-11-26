using System.Text;
using System.Text.RegularExpressions;
using Daksh.Platform.Security;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>What the chat assistant may look at in Google Drive and Slack, only when the question is about them and the owner connected them.</summary>
public static class WorkspaceAssistant
{
    private static readonly Regex DriveRx = new(@"\b(drive|google docs?|google sheets?|google slides?|spreadsheets?|sheets?|docs?|documents?|files?|presentations?|slides?|folders?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SlackRx = new(@"\b(slack|channels?|dms?|direct messages?|mentioned|mentions?|thread)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GitHubRx = new(@"\b(github|pull requests?|prs?|review(s|ed)?|issues?|commits?|repos?|repositor(y|ies)|merge)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotionRx = new(@"\b(notion|notes?|wiki|knowledge base|pages?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpotifyRx = new(@"\b(spotify|music|songs?|playing|playlists?|tracks?|listen(ed|ing)?|artist|album)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiscordRx = new(@"\b(discord)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static async Task<AssistantContextResult> BuildAsync(IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, string userId, string question, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var sources = new List<string>();
        var words = AssistantIntent.Keywords(question).Where(w => !AssistantIntent.MailWords.Contains(w)).Take(6).ToList();
        var query = string.Join(' ', words);

        if (DriveRx.IsMatch(question))
        {
            var files = await GoogleWorkspaceModule.FilesForAssistantAsync(userId, query.Length > 0 ? query : null, 8, db, protector, config, clients, ct);
            if (files is { Count: > 0 })
            {
                sb.AppendLine().AppendLine(query.Length > 0 ? "Google Drive files matching the question (newest first):" : "Recently opened Google Drive files:");
                foreach (var f in files) sb.AppendLine($"- {Clip(f.Name, 80)} ({f.Kind}, modified {f.Modified?.ToString("d MMM yyyy") ?? "unknown"}) {f.WebUrl}");
                sources.Add("Google Drive");
            }
            else if (files is not null) { sb.AppendLine().AppendLine("Google Drive is connected but no matching files were found."); sources.Add("Google Drive"); }
        }

        if (SlackRx.IsMatch(question) && await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.UserId == userId && c.IntegrationKey == SlackIntegrationModule.IntegrationKey && c.Status == ConnectionStatus.Connected).AnyAsync(ct))
        {
            var recent = await db.GetCollection<SlackActivity>("slackActivity").Find(a => a.UserId == userId).SortByDescending(a => a.CreatedAtUtc).Limit(8).ToListAsync(ct);
            if (recent.Count > 0)
            {
                sb.AppendLine().AppendLine("Recent Slack direct messages and mentions (newest first):");
                foreach (var a in recent) sb.AppendLine($"- {(a.Read ? "" : "[unread] ")}{(a.Kind == "dm" ? "DM" : "mention")} from {a.FromSlackUser}: {Clip(a.Text, 160)}");
                sources.Add("Slack");
            }
            if (query.Length > 0 && await SlackIntegrationModule.SearchForAssistantAsync(userId, query, 6, db, protector, clients, ct) is { Count: > 0 } hits)
            {
                sb.AppendLine().AppendLine("Slack messages matching the question:");
                foreach (var h in hits) sb.AppendLine($"- #{h.Channel} {h.UserName}: {Clip(h.Text, 160)}");
                if (!sources.Contains("Slack")) sources.Add("Slack");
            }
        }

        async Task Add(string app, string label, Regex rx, Func<Task<string?>> read)
        {
            if (!rx.IsMatch(question) || !await AppConnections.HasAsync(db, userId, app, ct)) return;
            if (await read() is { Length: > 0 } text) { sb.AppendLine().AppendLine(text); sources.Add(label); }
        }
        await Add(GitHubModule.Key, "GitHub", GitHubRx, () => GitHubModule.AssistantTextAsync(userId, db, protector, clients, ct));
        await Add(NotionModule.Key, "Notion", NotionRx, () => NotionModule.AssistantTextAsync(userId, query, db, protector, clients, ct));
        await Add(SpotifyModule.Key, "Spotify", SpotifyRx, () => SpotifyModule.AssistantTextAsync(userId, db, protector, config, clients, ct));
        await Add(DiscordModule.Key, "Discord", DiscordRx, () => DiscordModule.AssistantTextAsync(userId, db, protector, clients, ct));
        var more = await ProviderEngine.AssistantAsync(question, userId, db, protector, config, clients, ct);
        if (more.Text.Length > 0) { sb.Append(more.Text); sources.AddRange(more.Sources); }
        return new AssistantContextResult(sb.ToString(), sources);
    }
}
