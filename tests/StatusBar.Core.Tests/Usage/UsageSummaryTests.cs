using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Usage;

public class UsageSummaryTests
{
    [Fact]
    public void Principal_chooses_the_window_with_the_lowest_remaining_percentage()
    {
        var windows = new[]
        {
            new UsageWindow("session", "Session", 20, null),
            new UsageWindow("weekly", "Weekly", 90, null),
        };

        Assert.Equal("weekly", UsageSummary.Principal(windows)?.Key);
    }

    [Fact]
    public void Principal_uses_the_shorter_reset_when_remaining_percentages_tie()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var windows = new[]
        {
            new UsageWindow("weekly", "Weekly", 40, now.AddDays(7)),
            new UsageWindow("session", "Session", 40, now.AddHours(5)),
        };

        Assert.Equal("session", UsageSummary.Principal(windows)?.Key);
    }

    [Fact]
    public void Principal_returns_null_when_there_are_no_windows()
    {
        Assert.Null(UsageSummary.Principal(Array.Empty<UsageWindow>()));
    }

    [Theory]
    [InlineData(30, AllowanceLevel.Normal)]
    [InlineData(29.99, AllowanceLevel.Approaching)]
    [InlineData(10, AllowanceLevel.Approaching)]
    [InlineData(9.99, AllowanceLevel.Low)]
    public void Level_uses_the_30_and_10_percent_boundaries(double remainingPercent, AllowanceLevel expected)
    {
        Assert.Equal(expected, UsageSummary.Level(remainingPercent));
    }

    [Theory]
    [InlineData(-25, 100)]
    [InlineData(140, 0)]
    public void Remaining_percent_is_clamped_to_zero_through_one_hundred(double usedPercent, double expected)
    {
        var window = new UsageWindow("window", "Window", usedPercent, null);

        Assert.Equal(expected, window.RemainingPercent);
    }
}
