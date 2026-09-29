using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Usage;

public class UsagePaceTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Session_with_minutes_left_and_half_remaining_is_normal()
    {
        var window = new UsageWindow("session", "Session", 49, Now.AddMinutes(10), TimeSpan.FromHours(5));

        var pace = UsagePaceCalculator.Evaluate(window, Now);

        Assert.Equal(AllowanceLevel.Normal, pace?.Level);
        Assert.InRange(pace!.Value.TimeRemainingPercent, 3, 4);
    }

    [Fact]
    public void Weekly_half_used_with_most_of_the_week_left_is_approaching()
    {
        // 49% remaining, 5 days of 7 left (71%): 22 points behind the calendar.
        var window = new UsageWindow("weekly", "Weekly", 51, Now.AddDays(5), TimeSpan.FromDays(7));

        Assert.Equal(AllowanceLevel.Approaching, UsagePaceCalculator.Evaluate(window, Now)?.Level);
    }

    [Fact]
    public void Far_behind_the_calendar_is_low()
    {
        var window = new UsageWindow("weekly", "Weekly", 80, Now.AddDays(6), TimeSpan.FromDays(7));

        Assert.Equal(AllowanceLevel.Low, UsagePaceCalculator.Evaluate(window, Now)?.Level);
    }

    [Fact]
    public void Unknown_length_or_reset_has_no_pace()
    {
        Assert.Null(UsagePaceCalculator.Evaluate(new UsageWindow("a", "A", 10, Now.AddHours(1)), Now));
        Assert.Null(UsagePaceCalculator.Evaluate(new UsageWindow("a", "A", 10, null, TimeSpan.FromHours(5)), Now));
    }

    [Fact]
    public void Reset_in_the_past_clamps_to_zero_time_remaining()
    {
        var window = new UsageWindow("a", "A", 10, Now.AddHours(-1), TimeSpan.FromHours(5));

        Assert.Equal(0, UsagePaceCalculator.Evaluate(window, Now)?.TimeRemainingPercent);
    }
}
