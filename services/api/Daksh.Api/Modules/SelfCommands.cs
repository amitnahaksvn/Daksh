using System.Text.RegularExpressions;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public enum SelfCommandKind { None, Busy, Free, AddItem, RemoveItem, ShowLists }

public sealed record SelfCommand(SelfCommandKind Kind, string? List = null, string? Text = null, TimeSpan? Duration = null, string? UntilClock = null, string? Note = null);

/// <summary>
/// Commands the owner types in their OWN chat ("Message yourself" on WhatsApp, "Saved Messages" on Telegram): "busy for 2 hours", "free now",
/// "buy milk", "add headphones to amazon list", "remove milk", "show my lists". Plain keywords, no AI: fast, free and predictable.
/// Anything that isn't clearly a command is ignored, so ordinary notes to yourself are left alone.
/// </summary>
public static class SelfCommands
{
    /// <summary>Every reply we send starts with this invisible character, so our own messages are never read back as commands.</summary>
    public const string ReplyMarker = "​";

    private static readonly Regex Free = new(@"^\s*(i'?m|i am|am)?\s*(now )?(free|available|not busy|done|back)( now| again)?\s*[.!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BusyFor = new(@"^\s*(i'?m|i am)?\s*busy\s*(now)?\s*(for|next)?\s*(?<n>\d+(\.\d+)?|an?|half an?)\s*(?<u>h|hr|hrs|hour|hours|m|min|mins|minute|minutes)\b(?<note>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BusyUntil = new(@"^\s*(i'?m|i am)?\s*busy\s*(now)?\s*(until|till|til|upto)\s*(?<h>\d{1,2})(:(?<m>\d{2}))?\s*(?<ap>am|pm)?\b(?<note>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BusyBare = new(@"^\s*(i'?m|i am)?\s*busy( now)?\s*[.!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddToList = new(@"^\s*(add|put)\s+(?<t>.+?)\s+(to|in|into|on)\s+(my\s+|the\s+)?(?<l>.+?)(\s+list)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListColon = new(@"^\s*(?<l>[\p{L}][\p{L}\p{N} '-]{1,30}?)\s*(list)?\s*:\s*(?<t>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Buy = new(@"^\s*(buy|get|order|purchase|pick up|grab)\s+(?<t>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Todo = new(@"^\s*(todo|to-do|to do|task|remind me to|reminder)\s*[:\-]?\s*(?<t>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Remove = new(@"^\s*(remove|delete|cancel|undo|cross off|done with)\s+(?<t>.+?)(\s+from\s+(my\s+|the\s+)?(?<l>.+?)(\s+list)?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Show = new(@"^\s*(show|what'?s on|what is on|list|see)\s*(me\s+)?(my\s+|all\s+)?(lists?|todos?|to-?dos?|shopping|pending)\s*[?.!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SelfCommand Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 400 || t.StartsWith(ReplyMarker, StringComparison.Ordinal)) return new(SelfCommandKind.None);

        if (Free.IsMatch(t)) return new(SelfCommandKind.Free);
        if (Show.IsMatch(t)) return new(SelfCommandKind.ShowLists);

        var bf = BusyFor.Match(t);
        if (bf.Success)
        {
            var n = bf.Groups["n"].Value.ToLowerInvariant();
            var amount = n.StartsWith("half") ? 0.5 : n is "a" or "an" ? 1 : double.Parse(n, System.Globalization.CultureInfo.InvariantCulture);
            var minutes = bf.Groups["u"].Value.ToLowerInvariant().StartsWith('m') ? amount : amount * 60;
            if (minutes is > 0 and <= 24 * 60) return new(SelfCommandKind.Busy, Duration: TimeSpan.FromMinutes(minutes), Note: CleanNote(bf.Groups["note"].Value));
        }
        var bu = BusyUntil.Match(t);
        if (bu.Success)
        {
            var h = int.Parse(bu.Groups["h"].Value); var m = bu.Groups["m"].Success ? int.Parse(bu.Groups["m"].Value) : 0;
            var ap = bu.Groups["ap"].Value.ToLowerInvariant();
            if (ap == "pm" && h < 12) h += 12; else if (ap == "am" && h == 12) h = 0;
            if (h is >= 0 and < 24 && m is >= 0 and < 60) return new(SelfCommandKind.Busy, UntilClock: $"{h:00}:{m:00}", Note: CleanNote(bu.Groups["note"].Value));
        }
        if (BusyBare.IsMatch(t)) return new(SelfCommandKind.Busy, Duration: TimeSpan.FromHours(2));

        var rm = Remove.Match(t);
        if (rm.Success) return new(SelfCommandKind.RemoveItem, rm.Groups["l"].Success ? rm.Groups["l"].Value : null, rm.Groups["t"].Value.Trim());

        var add = AddToList.Match(t);
        if (add.Success) return new(SelfCommandKind.AddItem, ListsModule.CleanName(add.Groups["l"].Value), add.Groups["t"].Value.Trim());
        var buy = Buy.Match(t);
        if (buy.Success) return new(SelfCommandKind.AddItem, ListsModule.DefaultShopping, buy.Groups["t"].Value.Trim());
        var todo = Todo.Match(t);
        if (todo.Success) return new(SelfCommandKind.AddItem, ListsModule.DefaultTodo, todo.Groups["t"].Value.Trim());
        var lc = ListColon.Match(t);
        if (lc.Success && !Regex.IsMatch(lc.Groups["l"].Value, @"^(http|https|note|re|fwd|ps)$", RegexOptions.IgnoreCase))
            return new(SelfCommandKind.AddItem, ListsModule.CleanName(lc.Groups["l"].Value), lc.Groups["t"].Value.Trim());
        return new(SelfCommandKind.None);
    }

    /// <summary>"busy for 2 hours - in a meeting" → "in a meeting". Leading separators and an opening "because/for/in" are kept as written.</summary>
    private static string? CleanNote(string raw)
    {
        var n = Regex.Replace(raw ?? "", @"^[\s\-–—:,;.(]+|[\s.)]+$", "");
        return n.Length is > 0 and <= 120 ? n : null;
    }

    /// <summary>Runs a command for a user and returns the confirmation to send back, or null when the text wasn't a command.</summary>
    public static async Task<string?> RunAsync(IMongoDatabase db, string userId, string? text, DateTime utcNow, CancellationToken ct = default)
    {
        var cmd = Parse(text);
        if (cmd.Kind == SelfCommandKind.None) return null;
        var users = db.GetCollection<ApplicationUser>("users");

        string Reply(string s) => ReplyMarker + s;
        switch (cmd.Kind)
        {
            case SelfCommandKind.Busy:
            {
                var user = await users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
                if (user is null) return null;
                var b = user.Busy ?? new BusyProfile();
                DateTime until;
                if (cmd.UntilClock is { } clock)
                {
                    var tz = BusyMode.ResolveTimezone(b);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
                    var target = local.Date + TimeSpan.Parse(clock);
                    if (target <= local) target = target.AddDays(1);
                    until = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(target, DateTimeKind.Unspecified), tz);
                }
                else until = utcNow + cmd.Duration!.Value;
                b.Enabled = true; b.Until = until; b.Note = cmd.Note; // no note given = no stale note from last time
                await users.UpdateOneAsync(u => u.Id == userId, Builders<ApplicationUser>.Update.Set(u => u.Busy, b), cancellationToken: ct);
                var shown = TimeZoneInfo.ConvertTimeFromUtc(until, BusyMode.ResolveTimezone(b));
                var mode = b.ReplyMode == "Fixed" && !string.IsNullOrWhiteSpace(b.FixedMessage) ? "your fixed message" : "an AI reply";
                return Reply($"✅ You're busy until {shown:h:mm tt}{(cmd.Note is null ? "" : $" ({cmd.Note})")}. I'll reply for you with {mode} on your connected apps. Send \"free\" to stop.");
            }
            case SelfCommandKind.Free:
            {
                await users.UpdateOneAsync(u => u.Id == userId, Builders<ApplicationUser>.Update.Set("Busy.Until", (DateTime?)null).Set("Busy.Enabled", false).Set("Busy.Note", (string?)null), cancellationToken: ct);
                return Reply("✅ You're free. I've stopped replying for you.");
            }
            case SelfCommandKind.AddItem:
            {
                if (string.IsNullOrWhiteSpace(cmd.Text)) return null;
                var (item, count, dup) = await ListsModule.AddAsync(db, userId, cmd.List ?? ListsModule.DefaultTodo, cmd.Text, ct);
                return Reply(dup ? $"ℹ️ \"{item.Text}\" is already on your {item.List} list ({count} item{(count == 1 ? "" : "s")})."
                                 : $"✅ Added \"{item.Text}\" to your {item.List} list ({count} item{(count == 1 ? "" : "s")}). Send \"remove {item.Text}\" to undo.");
            }
            case SelfCommandKind.RemoveItem:
            {
                var removed = await ListsModule.RemoveByTextAsync(db, userId, cmd.List, cmd.Text ?? "", ct);
                return Reply(removed is null ? $"ℹ️ I couldn't find \"{cmd.Text}\" on your lists." : $"✅ Removed \"{removed.Text}\" from your {removed.List} list.");
            }
            case SelfCommandKind.ShowLists:
            {
                var open = await ListsModule.OpenAsync(db, userId, 60, ct);
                return Reply(open.Count == 0 ? "📋 Your lists are empty." : "📋 Your lists:\n" + ListsModule.Summarise(open));
            }
        }
        return null;
    }
}

/// <summary>Remembers which messages a command has already been run for, so a redelivered event can't add an item twice.</summary>
public static class SelfCommandSeen
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Seen = new();

    /// <summary>True the first time a (connection, message) is seen, false after.</summary>
    public static bool First(string connectionId, string messageId)
    {
        var now = DateTime.UtcNow;
        if (Seen.Count > 2000) foreach (var k in Seen.Where(kv => now - kv.Value > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList()) Seen.TryRemove(k, out _);
        return Seen.TryAdd($"{connectionId}:{messageId}", now);
    }
}
