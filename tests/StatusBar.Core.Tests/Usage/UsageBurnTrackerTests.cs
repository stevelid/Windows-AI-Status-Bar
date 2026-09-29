using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Usage;

public class UsageBurnTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    static UsageWindow Session(double used, DateTimeOffset resetsAt) =>
        new("five_hour", "5-hour limit", used, resetsAt, FiveHours);

    static UsageWindow Weekly(double used, DateTimeOffset resetsAt) =>
        new("seven_day", "Weekly limit", used, resetsAt, OneWeek);

    // Records one reading a minute for the given number of minutes, using `used(minute)`.
    static DateTimeOffset RecordMinutes(
        UsageBurnTracker tracker, int minutes, Func<int, UsageWindow> window, DateTimeOffset from)
    {
        for (var minute = 0; minute <= minutes; minute++)
            tracker.Record(UsageSource.Claude, [window(minute)], from.AddMinutes(minute));
        return from.AddMinutes(minutes);
    }

    [Fact]
    public void Burning_faster_than_the_time_left_is_approaching_with_both_times()
    {
        // 50% left, 3 h to reset, using 1% a minute (60%/h): empty in ~50 min, before the reset.
        var tracker = new UsageBurnTracker();
        var reset = Start.AddHours(3).AddMinutes(20);
        var now = RecordMinutes(tracker, 20, m => Session(30 + m, reset), Start);

        var runway = tracker.Assess(UsageSource.Claude, Session(50, reset), now);

        Assert.Equal(AllowanceLevel.Approaching, runway.Level);
        Assert.True(runway.RunsOutBeforeReset);
        Assert.InRange(runway.TimeToEmpty!.Value.TotalMinutes, 49, 51);
        Assert.Equal(TimeSpan.FromHours(3), runway.TimeToReset);
    }

    [Fact]
    public void Running_out_within_half_an_hour_is_low()
    {
        var tracker = new UsageBurnTracker();
        var reset = Start.AddHours(2);
        var now = RecordMinutes(tracker, 20, m => Session(60 + m, reset), Start);

        // 20% left at 60%/h: empty in 20 minutes.
        Assert.Equal(AllowanceLevel.Low, tracker.Assess(UsageSource.Claude, Session(80, reset), now).Level);
    }

    [Fact]
    public void Lasting_until_reset_stays_normal_even_when_usage_is_high()
    {
        // 25% left but only 10 minutes to reset at 6%/h: plenty.
        var tracker = new UsageBurnTracker();
        var reset = Start.AddMinutes(40);
        var now = RecordMinutes(tracker, 30, m => Session(72 + m * 0.1, reset), Start);

        var runway = tracker.Assess(UsageSource.Claude, Session(75, reset), now);

        Assert.Equal(AllowanceLevel.Normal, runway.Level);
        Assert.False(runway.RunsOutBeforeReset);
    }

    [Fact]
    public void Too_few_or_too_recent_readings_give_no_projection()
    {
        var tracker = new UsageBurnTracker();
        var reset = Start.AddHours(1);
        // Five minutes of heavy use is not enough evidence for a session window (needs 10).
        var now = RecordMinutes(tracker, 5, m => Session(10 + m * 5, reset), Start);

        var runway = tracker.Assess(UsageSource.Claude, Session(35, reset), now);

        Assert.Equal(AllowanceLevel.Normal, runway.Level);
        Assert.Null(runway.TimeToEmpty);
    }

    [Fact]
    public void A_reset_starts_the_history_again()
    {
        var tracker = new UsageBurnTracker();
        var firstReset = Start.AddMinutes(30);
        var now = RecordMinutes(tracker, 20, m => Session(60 + m, firstReset), Start);

        // The window resets: allowance jumps back and the reset time moves on by five hours.
        var nextReset = firstReset + FiveHours;
        tracker.Record(UsageSource.Claude, [Session(0, nextReset)], now.AddMinutes(1));
        var runway = tracker.Assess(UsageSource.Claude, Session(0, nextReset), now.AddMinutes(1));

        Assert.Null(runway.TimeToEmpty);
        Assert.Equal(AllowanceLevel.Normal, runway.Level);
    }

    [Fact]
    public void Weekly_window_uses_the_longer_look_back()
    {
        // 51% of the week left, 5 days to reset, using 2% an hour for 3 hours: empty in ~25 h.
        var tracker = new UsageBurnTracker();
        var reset = Start.AddDays(5).AddHours(3);
        for (var hour = 0; hour <= 3; hour++)
            tracker.Record(UsageSource.Codex, [Weekly(43 + hour * 2, reset)], Start.AddHours(hour));
        var now = Start.AddHours(3);

        var runway = tracker.Assess(UsageSource.Codex, Weekly(49, reset), now);

        Assert.Equal(AllowanceLevel.Approaching, runway.Level);
        Assert.InRange(runway.TimeToEmpty!.Value.TotalHours, 25, 26);
    }

    [Fact]
    public void Low_threshold_applies_without_rate_data_and_can_be_disabled()
    {
        var tracker = new UsageBurnTracker();
        var window = Session(95, Start.AddHours(4));

        Assert.Equal(AllowanceLevel.Low, tracker.Assess(UsageSource.Claude, window, Start, lowBelowPercent: 10).Level);
        Assert.Equal(AllowanceLevel.Normal, tracker.Assess(UsageSource.Claude, window, Start, lowBelowPercent: 0).Level);
    }

    [Fact]
    public void Worst_picks_the_window_that_will_run_out()
    {
        var tracker = new UsageBurnTracker();
        var sessionReset = Start.AddMinutes(45);
        var weeklyReset = Start.AddDays(4);
        for (var hour = 0; hour <= 2; hour++)
        {
            tracker.Record(UsageSource.Codex,
                [Session(40, sessionReset), Weekly(80 + hour * 5, weeklyReset)], Start.AddHours(hour));
        }

        var now = Start.AddHours(2);
        var (window, runway) = tracker.Worst(UsageSource.Codex,
            [Session(40, sessionReset), Weekly(90, weeklyReset)], now);

        Assert.Equal("seven_day", window!.Key);
        Assert.Equal(AllowanceLevel.Approaching, runway.Level);
    }
}
