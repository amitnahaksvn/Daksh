using Daksh.Platform.Automation;

namespace Daksh.Platform.Tests.Automation;

public sealed class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Next_AtSchedule_ReturnsTheRunTimeWhenStillInTheFuture()
    {
        var runAt = Now.AddHours(2);
        var spec = new ScheduleSpec("at", null, "UTC", null, runAt);
        Assert.Equal(runAt, ScheduleCalculator.Next(spec, Now, Now));
    }

    [Fact]
    public void Next_AtSchedule_ReturnsNullOncePast()
    {
        var runAt = Now.AddHours(-1);
        var spec = new ScheduleSpec("at", null, "UTC", null, runAt);
        Assert.Null(ScheduleCalculator.Next(spec, Now, Now));
    }

    [Fact]
    public void Next_EverySchedule_StaysOnTheAnchorGrid()
    {
        var anchor = Now;
        var spec = new ScheduleSpec("every", null, "UTC", 300, null); // every 5 minutes
        var after = anchor.AddMinutes(7); // between the 1st (5m) and 2nd (10m) grid point
        var next = ScheduleCalculator.Next(spec, after, anchor);
        Assert.Equal(anchor.AddMinutes(10), next);
    }

    [Fact]
    public void Next_EverySchedule_LateRunDoesNotShiftSubsequentOnes()
    {
        var anchor = Now;
        var spec = new ScheduleSpec("every", null, "UTC", 60, null); // every minute
        // Simulate a run that was very late (e.g. worker was down): "after" is way past several grid points.
        var after = anchor.AddMinutes(10).AddSeconds(30);
        var next = ScheduleCalculator.Next(spec, after, anchor);
        // Still lands exactly on the anchor's minute grid, not offset by the lateness.
        Assert.Equal(anchor.AddMinutes(11), next);
        Assert.Equal(0, next!.Value.Second);
    }

    [Fact]
    public void Next_EverySchedule_BeforeAnchor_ReturnsAnchor()
    {
        var anchor = Now.AddHours(1);
        var spec = new ScheduleSpec("every", null, "UTC", 60, null);
        Assert.Equal(anchor, ScheduleCalculator.Next(spec, Now, anchor));
    }

    [Fact]
    public void Next_CronSchedule_InUtc_ComputesNextOccurrence()
    {
        var spec = new ScheduleSpec("cron", "0 9 * * *", "UTC", null, null); // every day at 09:00 UTC
        var next = ScheduleCalculator.Next(spec, Now, Now);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_CronSchedule_InNonUtcTimeZone_ComputesCorrectUtcTime()
    {
        // 09:00 in Asia/Kolkata (UTC+5:30) is 03:30 UTC.
        var spec = new ScheduleSpec("cron", "0 9 * * *", "Asia/Kolkata", null, null);
        var after = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var next = ScheduleCalculator.Next(spec, after, after);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 3, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_CronSchedule_AcrossUsSpringForwardDst_SkipsTheNonExistentLocalHour()
    {
        // America/New_York springs forward on 2026-03-08: 02:00 local jumps to 03:00 local (02:xx does not exist).
        // A cron job scheduled for 02:30 local on that day should resolve to the next valid occurrence, one day later.
        var zoneId = "America/New_York";
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException) { return; } // environment without IANA tzdata installed; nothing to assert

        var spec = new ScheduleSpec("cron", "30 2 * * *", zoneId, null, null);
        var after = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero); // well before the transition
        var next = ScheduleCalculator.Next(spec, after, after);
        Assert.NotNull(next);
        // Convert back to local time in the target zone and confirm it never lands in the skipped 02:00-03:00 hour on the 8th.
        var local = TimeZoneInfo.ConvertTime(next!.Value, zone);
        if (local.Date == new DateTime(2026, 3, 8))
            Assert.True(local.Hour != 2, $"Expected the skipped local hour to be avoided, got {local}.");
    }

    [Fact]
    public void Next_UnknownScheduleKind_ReturnsNull()
    {
        var spec = new ScheduleSpec("bogus", null, "UTC", null, null);
        Assert.Null(ScheduleCalculator.Next(spec, Now, Now));
    }

    [Fact]
    public void Zone_UnknownTimeZoneId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Zone("Not/A_Real_Zone"));
    }

    [Fact]
    public void Zone_NullOrUtc_ReturnsUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, ScheduleCalculator.Zone(null));
        Assert.Equal(TimeZoneInfo.Utc, ScheduleCalculator.Zone("UTC"));
    }

    [Theory]
    [InlineData("at")]
    [InlineData("every")]
    [InlineData("cron")]
    public void Validate_ValidSpecForEachKind_ReturnsNoErrors(string kind)
    {
        var spec = kind switch
        {
            "at" => new ScheduleSpec("at", null, "UTC", null, Now.AddMinutes(5)),
            "every" => new ScheduleSpec("every", null, "UTC", 60, null),
            _ => new ScheduleSpec("cron", "0 * * * *", "UTC", null, null),
        };
        Assert.Empty(ScheduleCalculator.Validate(spec, Now));
    }

    [Fact]
    public void Validate_EveryBelowMinimum_ReturnsError()
    {
        var spec = new ScheduleSpec("every", null, "UTC", 1, null);
        Assert.NotEmpty(ScheduleCalculator.Validate(spec, Now));
    }

    [Fact]
    public void Validate_InvalidCronExpression_ReturnsError()
    {
        var spec = new ScheduleSpec("cron", "not a cron", "UTC", null, null);
        Assert.NotEmpty(ScheduleCalculator.Validate(spec, Now));
    }

    [Fact]
    public void Validate_UnknownTimeZone_ReturnsError()
    {
        var spec = new ScheduleSpec("every", null, "Not/A_Real_Zone", 60, null);
        Assert.NotEmpty(ScheduleCalculator.Validate(spec, Now));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void RetryDelay_DoublesEachAttempt(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ScheduleCalculator.RetryDelay(baseSeconds: 10, attempt));
    }

    [Fact]
    public void RetryDelay_IsCappedAtOneHour()
    {
        var delay = ScheduleCalculator.RetryDelay(baseSeconds: 60, attempt: 20);
        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Fact]
    public void RetryDelay_NeverBelowOneSecond()
    {
        var delay = ScheduleCalculator.RetryDelay(baseSeconds: 0, attempt: 1);
        Assert.True(delay >= TimeSpan.FromSeconds(1));
    }
}
