using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class BusyModeTests
{
    // Mon 2026-11-16 is a Monday. 06:00 UTC = 11:30 in Kolkata (UTC+5:30).
    private static BusyProfile S(Action<BusyProfile>? tweak = null)
    {
        var s = new BusyProfile { Enabled = true, Timezone = "Asia/Kolkata", Start = "09:00", End = "18:00", Days = [1, 2, 3, 4, 5] };
        tweak?.Invoke(s);
        return s;
    }
    private static DateTime Utc(int d, int h, int m = 0) => new(2026, 11, d, h, m, 0, DateTimeKind.Utc);

    [Fact] public void Busy_inside_the_window_in_the_owners_timezone() => Assert.True(BusyMode.IsBusy(S(), Utc(16, 6)));
    [Fact] public void The_timezone_decides_not_the_servers_clock() => Assert.False(BusyMode.IsBusy(S(), Utc(16, 13))); // 18:30 in Kolkata, but 13:00 UTC would be "inside" a UTC window
    [Fact] public void Not_busy_on_other_days() => Assert.False(BusyMode.IsBusy(S(), Utc(15, 6))); // Sunday
    [Fact] public void Off_means_never_busy() => Assert.False(BusyMode.IsBusy(S(s => s.Enabled = false), Utc(16, 6)));
    [Fact] public void Busy_until_overrides_the_schedule() => Assert.True(BusyMode.IsBusy(S(s => { s.Days = []; s.Until = Utc(15, 20); }), Utc(15, 19)));
    [Fact] public void Busy_until_in_the_past_does_nothing() => Assert.False(BusyMode.IsBusy(S(s => { s.Days = []; s.Until = Utc(15, 20); }), Utc(15, 21)));
    [Fact] public void An_overnight_window_covers_the_next_morning() =>
        Assert.True(BusyMode.IsBusy(S(s => { s.Start = "22:00"; s.End = "06:00"; s.Days = [1]; }), Utc(17, 0, 0))); // Tue 05:30 IST, started Monday
    [Fact] public void A_bad_timezone_falls_back_to_UTC_instead_of_throwing() => Assert.True(BusyMode.IsBusy(S(s => { s.Timezone = "Nowhere/Land"; s.Start = "05:00"; s.End = "07:00"; }), Utc(16, 6)));
    [Fact] public void No_profile_means_not_busy() => Assert.False(BusyMode.IsBusy(null, Utc(16, 6)));

    [Fact] public void Fixed_mode_uses_the_message_and_AI_mode_does_not()
    {
        Assert.Equal("In a meeting, back soon.", BusyMode.FixedReply(S(s => { s.ReplyMode = "Fixed"; s.FixedMessage = "  In a meeting, back soon. "; })));
        Assert.Null(BusyMode.FixedReply(S(s => { s.ReplyMode = "Ai"; s.FixedMessage = "x"; })));
        Assert.Null(BusyMode.FixedReply(S(s => { s.ReplyMode = "Fixed"; s.FixedMessage = " "; })));
    }
}
