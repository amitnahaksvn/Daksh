using System.Text.RegularExpressions;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>Where the owner wants one-time codes from email copied to. Off by default; only ever sent to the owner's OWN chat.</summary>
public sealed class OtpForwardProfile
{
    public bool Enabled { get; set; }
    public bool WhatsApp { get; set; } = true;
    public bool Telegram { get; set; } = true;
}

public sealed record OtpForwardDto(bool Enabled, bool WhatsApp, bool Telegram, bool WhatsAppConnected, bool TelegramConnected);
public sealed record UpdateOtpForwardRequest(bool? Enabled, bool? WhatsApp, bool? Telegram);

public sealed class OtpForwardedDoc
{
    [BsonId] public string Key { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Finds a one-time code in an email. Pure, so it is tested directly.</summary>
public static class OtpExtractor
{
    private static readonly Regex Marker = new(@"\b(otp|one[- ]time (pass(word|code)|code)|verification code|security code|login code|sign[- ]?in code|authentication code|confirmation code|passcode)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex After = new(@"(?:code|otp|passcode|pin)[^0-9A-Za-z]{0,25}(?:is|:|-|=)?[^0-9A-Za-z]{0,6}(?<c>\d{4,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Before = new(@"\b(?<c>\d{4,8})\b[^.\n]{0,25}\b(is|as) your\b[^.\n]{0,30}(code|otp|passcode|pin)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaced = new(@"(?:code|otp)[^0-9]{0,20}(?<c>\d{3}[ -]\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the email says it carries a one-time code.</summary>
    public static bool LooksLikeOtp(string? subject, string? body) => Marker.IsMatch(subject ?? "") || Marker.IsMatch(body ?? "");

    /// <summary>The code, or null when the text doesn't clearly contain one.</summary>
    public static string? Extract(string? subject, string? body)
    {
        var text = (subject ?? "") + "\n" + (body ?? "");
        if (text.Length > 6000) text = text[..6000];
        foreach (var rx in new[] { Before, After, Spaced })
        {
            var m = rx.Match(text);
            if (!m.Success) continue;
            var code = Regex.Replace(m.Groups["c"].Value, @"[ -]", "");
            if (code.Length is >= 4 and <= 8 && !Regex.IsMatch(code, @"^(19|20)\d\d$")) return code; // not a year
        }
        return null;
    }
}

public static class OtpForwarder
{
    public static async Task<OtpForwardProfile> LoadAsync(IMongoDatabase db, string userId, CancellationToken ct) =>
        (await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == userId).FirstOrDefaultAsync(ct))?.OtpForward ?? new OtpForwardProfile();

    /// <summary>Copies a code from email to the owner's own WhatsApp / Telegram chat, once per email. Returns how many chats it was sent to.</summary>
    public static async Task<int> ForwardAsync(IServiceProvider sp, IMongoDatabase db, string userId, string messageKey, string from, string code, DateTimeOffset receivedAt, ILogger log, CancellationToken ct)
    {
        var profile = await LoadAsync(db, userId, ct);
        if (!profile.Enabled || (!profile.WhatsApp && !profile.Telegram)) return 0;
        if (DateTimeOffset.UtcNow - receivedAt > TimeSpan.FromMinutes(20)) return 0; // an old code is useless, and a sync catching up must not spam

        var done = db.GetCollection<OtpForwardedDoc>("otpForwarded");
        try { await done.InsertOneAsync(new OtpForwardedDoc { Key = $"{userId}:{messageKey}" }, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return 0; }

        var who = Regex.Replace(from, @"<.*?>", "").Trim().Trim('"');
        var text = $"{SelfCommands.ReplyMarker}🔐 {code}\nOTP from {(who.Length == 0 ? "an email" : who)}. Don't share it with anyone.";
        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == userId && c.Status == ConnectionStatus.Connected).ToListAsync(ct);
        var sent = 0;

        if (profile.WhatsApp)
            foreach (var c in connections.Where(c => c.IntegrationKey == "whatsapp" && !string.IsNullOrEmpty(c.AccountIdentifier)).Take(1))
                try
                {
                    var self = c.AccountIdentifier!.Split(':')[0].Split('@')[0] + "@s.whatsapp.net";
                    await sp.GetRequiredService<WhatsAppIntegrationProvider>().SendMessageAsync(c.Id, self, text, quotedMessageId: null, ct, isAiGenerated: true);
                    sent++;
                }
                catch (Exception ex) { log.LogWarning(ex, "OTP forward to WhatsApp failed for user {UserId}", userId); }

        if (profile.Telegram)
            foreach (var c in connections.Where(c => c.IntegrationKey == "telegram" && !string.IsNullOrEmpty(c.SelfChatId)).Take(1))
                try
                {
                    await TelegramIntegrationModule.SendAndStoreAsync(db, sp.GetRequiredService<TelegramIntegrationProvider>(), c.Id, c.SelfChatId!, text, isAiGenerated: true, ct);
                    sent++;
                }
                catch (Exception ex) { log.LogWarning(ex, "OTP forward to Telegram failed for user {UserId}", userId); }
        return sent;
    }
}
