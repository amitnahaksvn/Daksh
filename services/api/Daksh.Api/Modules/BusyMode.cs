using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>
/// The account owner's "I'm busy" profile. It belongs to the person, not to one messaging app: while it is active, auto-reply switches
/// itself on for EVERY connected app (WhatsApp, Telegram…) even if that app's own auto-reply switch is off.
/// </summary>
public sealed class BusyProfile
{
    public bool Enabled { get; set; }
    /// <summary>IANA timezone of where the owner is right now (e.g. "Asia/Kolkata"). Empty = UTC.</summary>
    public string Timezone { get; set; } = "";
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "18:00";
    /// <summary>Days (0 = Sunday … 6 = Saturday) the daily window applies to. Empty = no daily window, only <see cref="Until"/>.</summary>
    public List<int> Days { get; set; } = [];
    /// <summary>"Busy right now until…" — a one-off override. Null or in the past = not set.</summary>
    public DateTime? Until { get; set; }
    public string ReplyMode { get; set; } = "Ai"; // "Ai" | "Fixed"
    public string? FixedMessage { get; set; }
    /// <summary>Optional: why/what, e.g. "in a meeting" or "driving". The owner chose to share it, so replies may mention it.</summary>
    public string? Note { get; set; }
}

/// <summary>Decides whether the owner is busy. Pure, so it can be tested.</summary>
public static class BusyMode
{
    public static bool IsValidTimezone(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; } catch { return false; }
    }

    public static TimeZoneInfo ResolveTimezone(BusyProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Timezone))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(p.Timezone); } catch { /* fall through to UTC */ }
        }
        return TimeZoneInfo.Utc;
    }

    public static bool IsBusy(BusyProfile? p, DateTime utcNow)
    {
        if (p is null || !p.Enabled) return false;
        if (p.Until is { } until && until > utcNow) return true;
        if (p.Days.Count == 0) return false;
        if (!TimeSpan.TryParse(p.Start, out var start) || !TimeSpan.TryParse(p.End, out var end) || start == end) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ResolveTimezone(p));
        var now = local.TimeOfDay;
        var day = (int)local.DayOfWeek;
        // The selected day is the one the window starts on, so an overnight Mon 22:00–06:00 window still covers Tuesday's small hours.
        bool DayOn(int d) => p.Days.Contains(((d % 7) + 7) % 7);
        if (start < end) return DayOn(day) && now >= start && now < end;
        return (now >= start && DayOn(day)) || (now < end && DayOn(day - 1));
    }

    /// <summary>The fixed message to send while busy, or null to let the AI answer (mode "Ai", or "Fixed" with no message written yet).</summary>
    public static string? FixedReply(BusyProfile? p) =>
        p is { ReplyMode: "Fixed" } && !string.IsNullOrWhiteSpace(p.FixedMessage) ? p.FixedMessage.Trim() : null;

    /// <summary>The optional note to share while busy, only when the owner is actually busy.</summary>
    public static string? ActiveNote(BusyProfile? p, DateTime utcNow) => IsBusy(p, utcNow) && !string.IsNullOrWhiteSpace(p!.Note) ? p.Note!.Trim() : null;

    /// <summary>The busy profile of the account that owns a connection. Never throws: no profile means "not busy".</summary>
    public static async Task<BusyProfile?> LoadAsync(IMongoDatabase db, string userId, CancellationToken ct = default)
    {
        try { return (await db.GetCollection<ApplicationUser>("users").Find(u => u.Id == userId).FirstOrDefaultAsync(ct))?.Busy; }
        catch { return null; }
    }
}

public sealed record BusyProfileDto(bool Enabled, string Timezone, string Start, string End, IReadOnlyList<int> Days, DateTime? Until, string ReplyMode, string? FixedMessage, bool IsBusyNow, string? Note = null);
public sealed record UpdateBusyProfileRequest(bool? Enabled, string? Timezone, string? Start, string? End, List<int>? Days, DateTime? Until, bool? ClearUntil, string? ReplyMode, string? FixedMessage, string? Note = null);
