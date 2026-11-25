using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using MongoDB.Driver;
using static Daksh.Api.Modules.AppConnections;

namespace Daksh.Api.Modules;

// Discord: the person creates their own bot in the Discord Developer Portal and pastes its token here (like an Azure DevOps token). The
// token is checked against Discord, stored encrypted and never shown again. The bot reads and posts only in the servers it was invited to.
// Reading is on by default; "full access" is Daksha's own switch for posting as the bot. There is no server-side key to configure.

public sealed record ConnectDiscordRequest(string? Token);
public sealed record DiscordGuildDto(string Id, string Name, string? Icon);
public sealed record DiscordChannelDto(string Id, string Name, string Kind, int Position, string? Topic);
public sealed record DiscordMessageDto(string Id, string Author, bool IsBot, string Content, DateTimeOffset? Time, int Attachments, int Embeds);
public sealed record DiscordMessagePage(IReadOnlyList<DiscordMessageDto> Messages, string? Older);
public sealed record SendDiscordMessageRequest(string? Content);
public sealed record DiscordInviteDto(string? Url, string? ApplicationId);

public static class DiscordModule
{
    public const string Key = "discord";
    private const string TokenKey = "discord-bot-token";
    private static readonly Regex IdPattern = new(@"^\d{15,25}$", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"^[A-Za-z0-9._\-]{50,100}$", RegexOptions.Compiled);
    /// <summary>View channels, read message history and send messages: the least a reading-and-replying bot needs.</summary>
    private const int InvitePermissions = 1024 + 65536 + 2048;

    public static readonly AppInfo Info = new(Key, "Discord",
    [
        new("itemCount", "int", "3", 3, 10),
        new("hideBots", "bool", "false"),
        new("watchedChannels", "text", "", Max: 130),
    ], _ => true, SoftWrite: true);

    public static IServiceCollection AddDiscordIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("discord-api", c => { c.BaseAddress = new Uri("https://discord.com/api/v10/"); c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DakshaBot", "1.0")); });
        return services;
    }

    public static void MapDiscord(this IEndpointRouteBuilder app)
    {
        var a = app.MapGroup("/api/integrations/discord").WithTags("Discord").RequireAuthorization(AuthSetup.UserScheme);
        a.MapCommon(Info);
        a.MapPost("/connect", ConnectAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapGet("/invite", InviteAsync);
        a.MapGet("/guilds", GuildsAsync);
        a.MapGet("/guilds/{guildId}/channels", ChannelsAsync);
        a.MapGet("/channels/{channelId}/messages", MessagesAsync);
        a.MapPost("/channels/{channelId}/messages", SendAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- connect -------------------------------------------------------------------------------

    private sealed record Ctx(AppAccountDoc Account, string Token, IMongoDatabase Db, IHttpClientFactory Clients);

    private static async Task<JsonDocument> DiscordAsync(string token, IHttpClientFactory clients, string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await clients.CreateClient("discord-api").SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var text429 = await res.Content.ReadAsStringAsync(ct);
                double wait = 2;
                try { using var d = JsonDocument.Parse(text429); if (d.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var s)) wait = s; } catch (JsonException) { }
                if (attempt < 2 && wait <= 10) { await Task.Delay(TimeSpan.FromSeconds(wait), ct); continue; }
                throw new AppException(429, "Discord is rate limiting requests. Please try again in a moment.", "rate_limited");
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new AppException(409, "Discord did not accept the bot token. It may have been reset: add the new token in Manage.", "reconnect_required");
            if (res.StatusCode == HttpStatusCode.Forbidden) throw new AppException(403, "The bot is not allowed to do that here. Check it was invited to the server with permission to view the channel, read history and send messages.", "forbidden");
            if (res.StatusCode == HttpStatusCode.NotFound) throw new AppException(404, "Discord could not find that. The bot may not be in that server or channel.", "not_found");
            if (!res.IsSuccessStatusCode) throw new AppException(res.StatusCode == HttpStatusCode.BadRequest ? 400 : 502, "Discord could not complete the request.");
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }

    private static async Task<IResult> ConnectAsync(ConnectDiscordRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => await GuardAsync("Discord", async () =>
    {
        var token = r.Token?.Trim();
        if (string.IsNullOrEmpty(token) || !TokenPattern.IsMatch(token)) return Results.Problem(statusCode: 400, title: "Paste the bot token exactly as the Discord Developer Portal showed it.");
        string botId, botName, appId;
        try
        {
            using var me = await DiscordAsync(token, clients, "users/@me", ct: ct);
            botId = Str(me.RootElement, "id") ?? "";
            botName = Str(me.RootElement, "username") ?? "Discord bot";
            if (botId.Length == 0 || !Flag(me.RootElement, "bot")) return Results.Problem(statusCode: 400, title: "That is not a bot token. Use the token from the Bot page of your application.");
            using var app = await DiscordAsync(token, clients, "oauth2/applications/@me", ct: ct);
            appId = Str(app.RootElement, "id") ?? botId;
        }
        catch (AppException ex) when (ex.StatusCode == 409) { return Results.Problem(statusCode: 400, title: "Discord did not accept that token. Check it has not been reset."); }
        var existing = await LoadAccountAsync(db, who.UserId, Key, ct);
        var connection = await SaveAccountAsync(db, who.UserId, Key, botId, botName, existing?.Write ?? false, "", new() { ["applicationId"] = appId }, ct);
        await SaveSecretAsync(db, protector, connection.Id, TokenKey, token, ct);
        return Results.NoContent();
    });

    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is not null) await RemoveAsync(db, account, ct); // the bot itself stays yours: remove it from servers or reset its token in the Developer Portal
        return Results.NoContent();
    }

    private static async Task<Ctx> RequireAsync(string userId, bool write, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, userId, Key, ct) ?? throw new AppException(409, "Connect a Discord bot to continue.", "not_connected");
        if (write && !account.Write) throw new AppException(403, "Daksha has read-only access to Discord. Turn on full access in Manage to post messages.", "write_required");
        var token = await LoadSecretAsync(db, protector, account.ConnectionId, TokenKey, ct) ?? throw new AppException(409, "The Discord bot token is missing. Add it again.", "not_connected");
        return new Ctx(account, token, db, clients);
    }

    private static Task<IResult> InviteAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) => GuardAsync("Discord", async () =>
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        var appId = account?.Extra.GetValueOrDefault("applicationId");
        return Results.Ok(new DiscordInviteDto(appId is null ? null : $"https://discord.com/oauth2/authorize?client_id={appId}&scope=bot&permissions={InvitePermissions}", appId));
    });

    // ---- servers, channels, messages -----------------------------------------------------------

    private static Task<IResult> GuildsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Discord", async () =>
    {
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var doc = await DiscordAsync(c.Token, clients, "users/@me/guilds?limit=100", ct: ct);
        return Results.Ok(doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(g => new DiscordGuildDto(Str(g, "id") ?? "", Str(g, "name") ?? "", Str(g, "icon"))).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray() : []);
    });

    private static Task<IResult> ChannelsAsync(string guildId, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Discord", async () =>
    {
        if (!IdPattern.IsMatch(guildId)) return Results.BadRequest("That server is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        using var doc = await DiscordAsync(c.Token, clients, $"guilds/{guildId}/channels", ct: ct);
        // 0 = text, 5 = announcement. Voice, categories and forums have no plain message history to read.
        return Results.Ok(doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Where(ch => Num(ch, "type") is 0 or 5)
            .Select(ch => new DiscordChannelDto(Str(ch, "id") ?? "", Str(ch, "name") ?? "", Num(ch, "type") == 5 ? "announcement" : "text", Num(ch, "position"), Str(ch, "topic"))).OrderBy(ch => ch.Position).ToArray() : []);
    });

    private static List<DiscordMessageDto> ToMessages(JsonElement arr, bool hideBots) =>
        arr.ValueKind != JsonValueKind.Array ? [] : arr.EnumerateArray().Select(m =>
        {
            var author = Obj(m, "author");
            var content = Str(m, "content") ?? "";
            if (content.Length == 0 && m.TryGetProperty("embeds", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) content = Str(e[0], "title") ?? Str(e[0], "description") ?? "";
            return new DiscordMessageDto(Str(m, "id") ?? "", Str(author, "global_name") ?? Str(author, "username") ?? "Someone", Flag(author, "bot"), content, Date(Str(m, "timestamp")),
                m.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0, m.TryGetProperty("embeds", out var em) && em.ValueKind == JsonValueKind.Array ? em.GetArrayLength() : 0);
        }).Where(m => !(hideBots && m.IsBot)).Reverse().ToList();

    private static Task<IResult> MessagesAsync(string channelId, string? before, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Discord", async () =>
    {
        if (!IdPattern.IsMatch(channelId) || (before is not null && !IdPattern.IsMatch(before))) return Results.BadRequest("That channel is not valid.");
        var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
        var s = await LoadSettingsAsync(db, Info, who.UserId, ct);
        using var doc = await DiscordAsync(c.Token, clients, $"channels/{channelId}/messages?limit=40{(before is null ? "" : "&before=" + before)}", ct: ct);
        var raw = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? Str(doc.RootElement[doc.RootElement.GetArrayLength() - 1], "id") : null;
        var more = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() >= 40 ? raw : null;
        return Results.Ok(new DiscordMessagePage(ToMessages(doc.RootElement, Bool(s, "hideBots")), more));
    });

    private static Task<IResult> SendAsync(string channelId, SendDiscordMessageRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct) => GuardAsync("Discord", async () =>
    {
        if (!IdPattern.IsMatch(channelId)) return Results.BadRequest("That channel is not valid.");
        var content = r.Content?.Trim();
        if (string.IsNullOrEmpty(content)) return Results.Problem(statusCode: 400, title: "Write a message first.");
        if (content.Length > 2000) return Results.Problem(statusCode: 400, title: "Discord messages can be at most 2,000 characters.");
        var c = await RequireAsync(who.UserId, true, db, protector, clients, ct);
        using var doc = await DiscordAsync(c.Token, clients, $"channels/{channelId}/messages", HttpMethod.Post, new { content, allowed_mentions = new { parse = Array.Empty<string>() } }, ct);
        return Results.Ok(new { id = Str(doc.RootElement, "id") });
    });

    // ---- dashboard and assistant ---------------------------------------------------------------

    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        var account = await LoadAccountAsync(db, who.UserId, Key, ct);
        if (account is null) return Results.Ok(new AppDashboardDto(false, null, [], []));
        var errors = new List<string>();
        var sections = new List<DashSection>();
        try
        {
            var c = await RequireAsync(who.UserId, false, db, protector, clients, ct);
            var s = await LoadSettingsAsync(db, Info, who.UserId, ct);
            var count = Int(s, "itemCount", 3);
            var watched = Text(s, "watchedChannels").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => IdPattern.IsMatch(id)).Take(5).ToList();
            foreach (var channel in watched)
            {
                try
                {
                    using var meta = await DiscordAsync(c.Token, clients, $"channels/{channel}", ct: ct);
                    using var doc = await DiscordAsync(c.Token, clients, $"channels/{channel}/messages?limit={count + 5}", ct: ct);
                    var messages = ToMessages(doc.RootElement, Bool(s, "hideBots")).TakeLast(count).Reverse();
                    sections.Add(new DashSection($"#{Str(meta.RootElement, "name") ?? channel}", "/apps/discord",
                        messages.Select(m => new DashItem(m.Id, Clip(m.Content, 100) is { Length: > 0 } t ? t : "(no text)", $"{m.Author} · {m.Time?.ToLocalTime():d MMM HH:mm}", null, m.IsBot ? "bot" : null)).ToArray()));
                }
                catch (AppException ex) { errors.Add($"#{channel}: {ex.Message}"); }
            }
            if (watched.Count == 0)
            {
                using var guilds = await DiscordAsync(c.Token, clients, "users/@me/guilds?limit=100", ct: ct);
                var list = guilds.RootElement.ValueKind == JsonValueKind.Array ? guilds.RootElement.EnumerateArray().Select(g => new DashItem(Str(g, "id") ?? "", Str(g, "name") ?? "", null, null, null)).Take(count + 3).ToArray() : [];
                sections.Add(new DashSection("Servers", "/apps/discord", list));
            }
        }
        catch (AppException ex) { errors.Add(ex.Message); }
        catch (HttpRequestException) { errors.Add("Discord could not be reached."); }
        return Results.Ok(new AppDashboardDto(true, account.AccountName, sections, errors));
    }

    /// <summary>The latest messages in the channels the owner chose to watch, for the chat assistant. Null when Discord is not connected or nothing is watched.</summary>
    internal static async Task<string?> AssistantTextAsync(string userId, IMongoDatabase db, ISecretProtector protector, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(userId, false, db, protector, clients, ct);
            var s = await LoadSettingsAsync(db, Info, userId, ct);
            var watched = Text(s, "watchedChannels").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => IdPattern.IsMatch(id)).Take(3).ToList();
            if (watched.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var channel in watched)
            {
                using var meta = await DiscordAsync(c.Token, clients, $"channels/{channel}", ct: ct);
                using var doc = await DiscordAsync(c.Token, clients, $"channels/{channel}/messages?limit=10", ct: ct);
                sb.AppendLine($"Latest Discord messages in #{Str(meta.RootElement, "name") ?? channel} (oldest first):");
                foreach (var m in ToMessages(doc.RootElement, Bool(s, "hideBots"))) sb.AppendLine($"- {m.Author}: {Clip(m.Content, 140)}");
            }
            return sb.ToString();
        }
        catch (AppException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}
