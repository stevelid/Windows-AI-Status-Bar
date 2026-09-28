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
    public void Compact_prefers_the_shortest_window_even_when_a_longer_one_is_tighter()
    {
        var windows = new[]
        {
            new UsageWindow("weekly", "Weekly", 90, null, TimeSpan.FromDays(7)),
            new UsageWindow("session", "Session", 20, null, TimeSpan.FromHours(5)),
        };

        Assert.Equal("session", UsageSummary.Compact(windows)?.Key);
    }

    [Fact]
    public void Compact_falls_back_to_the_tightest_window_when_no_lengths_are_known()
    {
        var windows = new[]
        {
            new UsageWindow("a", "A", 20, null),
            new UsageWindow("b", "B", 90, null),
        };

        Assert.Equal("b", UsageSummary.Compact(windows)?.Key);
    }

    [Fact]
    public void Compact_returns_null_when_there_are_no_windows()
    {
        Assert.Null(UsageSummary.Compact(Array.Empty<UsageWindow>()));
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
