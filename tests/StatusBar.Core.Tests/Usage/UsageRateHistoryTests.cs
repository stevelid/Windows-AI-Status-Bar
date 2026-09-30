using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Usage;

public sealed class UsageRateHistoryTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    readonly string _directory = Path.Combine(Path.GetTempPath(), "statusbar-rate-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_directory, "usage-history.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    static UsageWindow Session(double used, string key = "primary", TimeSpan? length = null) =>
        new(key, "5-hour limit", used, Now.AddHours(3), length ?? TimeSpan.FromHours(5));

    static (DateTimeOffset At, double Remaining)[] Steady(double startRemaining, double pointsPerHour, int minutes, int everyMinutes = 2) =>
        Enumerable.Range(0, minutes / everyMinutes + 1)
            .Select(i => (Now - TimeSpan.FromMinutes(minutes - i * everyMinutes), startRemaining - pointsPerHour * (i * everyMinutes) / 60.0))
            .ToArray();

    [Fact]
    public void A_steady_burn_gives_a_flat_series_at_that_rate()
    {
        var series = UsageRateHistory.Compute(Steady(90, 20, 120), Now);

        Assert.True(series.HasData);
        Assert.Equal(20, series.PeakPerHour!.Value, precision: 0);
        Assert.All(series.Points.Where(point => point.PerHour is not null), point => Assert.InRange(point.PerHour!.Value, 18, 22));
        Assert.Equal(20, series.Points.Count);
        Assert.Equal(Now - UsageRateHistory.Span + UsageRateHistory.Bucket / 2, series.Points[0].At);
    }

    [Fact]
    public void Idle_time_is_zero_not_missing_and_a_burst_shows_as_a_peak()
    {
        var readings = new List<(DateTimeOffset At, double Remaining)>();
        var remaining = 80.0;
        for (var minute = -120; minute <= 0; minute += 2)
        {
            // Idle for the first hour, then 30 points an hour for 30 minutes, then idle.
            if (minute is > -60 and <= -30) remaining -= 1.0;
            readings.Add((Now.AddMinutes(minute), Math.Round(remaining)));
        }

        var series = UsageRateHistory.Compute(readings, Now);

        Assert.Equal(0, series.Points[0].PerHour);
        Assert.Equal(0, series.Points[^1].PerHour);
        Assert.InRange(series.PeakPerHour!.Value, 25, 35);
        Assert.InRange((Now - series.PeakAt!.Value).TotalMinutes, 30, 60);
    }

    [Fact]
    public void A_gap_in_readings_is_missing_data_not_a_quiet_spell()
    {
        var readings = new[]
        {
            (Now.AddMinutes(-119), 90.0), (Now.AddMinutes(-117), 89.0), (Now.AddMinutes(-115), 89.0),
            // The app was closed for over an hour.
            (Now.AddMinutes(-20), 70.0), (Now.AddMinutes(-18), 70.0), (Now.AddMinutes(-16), 69.0),
        };

        var series = UsageRateHistory.Compute(readings, Now);

        Assert.Null(series.Points[8].PerHour);
        Assert.NotNull(series.Points[0].PerHour);
        Assert.NotNull(series.Points[^3].PerHour);
        Assert.Null(series.Points[^1].PerHour);
    }

    [Fact]
    public void A_rise_in_allowance_is_a_reset_and_costs_nothing()
    {
        var readings = new[]
        {
            (Now.AddMinutes(-30), 10.0), (Now.AddMinutes(-28), 9.0), (Now.AddMinutes(-26), 100.0),
            (Now.AddMinutes(-24), 100.0), (Now.AddMinutes(-22), 99.0),
        };

        var series = UsageRateHistory.Compute(readings, Now);

        Assert.Equal(Now.AddMinutes(-26), Assert.Single(series.Resets));
        Assert.True(series.PeakPerHour < 40);
    }

    [Fact]
    public void Nothing_recorded_gives_no_series_and_unknown_windows_are_ignored()
    {
        var history = new UsageRateHistory(new FakeTimeProvider(Now), FilePath, persist: false);

        Assert.Null(history.Series(UsageSource.Codex, "primary"));

        history.Record(UsageSource.Codex, [Session(50, "weekly", length: TimeSpan.FromDays(7)), Session(50, "nolength", length: null) with { Length = null }], Now);
        Assert.Null(history.Series(UsageSource.Codex, "weekly"));
        Assert.Null(history.Series(UsageSource.Codex, "nolength"));
    }

    [Fact]
    public void Recording_builds_a_series_and_ignores_a_repeated_moment()
    {
        var time = new FakeTimeProvider(Now);
        var history = new UsageRateHistory(time, FilePath, persist: false);

        for (var minute = 120; minute >= 0; minute -= 2)
            history.Record(UsageSource.Claude, [Session(10 + (120 - minute) / 6.0)], Now.AddMinutes(-minute));
        history.Record(UsageSource.Claude, [Session(99)], Now);

        var series = history.Series(UsageSource.Claude, "primary")!;
        Assert.True(series.HasData);
        Assert.InRange(series.PeakPerHour!.Value, 8, 12);
        Assert.Null(history.Series(UsageSource.Codex, "primary"));
    }

    [Fact]
    public void Readings_older_than_the_retention_are_dropped()
    {
        var time = new FakeTimeProvider(Now);
        var history = new UsageRateHistory(time, FilePath, persist: true);
        history.Record(UsageSource.Codex, [Session(10)], Now.AddHours(-5));
        history.Record(UsageSource.Codex, [Session(20)], Now.AddHours(-2));
        history.Record(UsageSource.Codex, [Session(30)], Now);

        // Force a write, then count what was kept: the five-hour-old reading is gone, the other two stay.
        time.Advance(TimeSpan.FromMinutes(1));
        history.Record(UsageSource.Codex, [Session(31)], Now.AddMinutes(1));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
        var kept = document.RootElement.GetProperty("Windows")[0].GetProperty("Readings");
        Assert.Equal(3, kept.GetArrayLength());
        Assert.All(kept.EnumerateArray(), reading =>
            Assert.True(DateTimeOffset.FromUnixTimeSeconds((long)reading[0].GetDouble()) >= Now.AddHours(-3)));
    }

    [Fact]
    public void Nothing_is_written_unless_persistence_is_on_and_history_survives_a_restart()
    {
        var time = new FakeTimeProvider(Now);
        var off = new UsageRateHistory(time, FilePath, persist: false);
        off.Record(UsageSource.Codex, [Session(10)], Now);
        Assert.False(File.Exists(FilePath));

        var on = new UsageRateHistory(time, FilePath, persist: true);
        for (var minute = 60; minute >= 0; minute -= 2)
            on.Record(UsageSource.Codex, [Session(20 + (60 - minute) / 3.0)], Now.AddMinutes(-minute));
        on.SetPersistence(false);
        Assert.False(File.Exists(FilePath));
        on.SetPersistence(true);
        Assert.True(File.Exists(FilePath));

        var restarted = new UsageRateHistory(time, FilePath, persist: true);
        var series = restarted.Series(UsageSource.Codex, "primary")!;
        Assert.True(series.HasData);
        Assert.InRange(series.PeakPerHour!.Value, 15, 25);
    }

    [Fact]
    public void The_file_holds_only_percentages_and_times_and_writes_are_throttled()
    {
        var time = new FakeTimeProvider(Now);
        var history = new UsageRateHistory(time, FilePath, persist: true);
        history.Record(UsageSource.Codex, [Session(10)], Now.AddMinutes(-3));
        var first = File.ReadAllText(FilePath);
        Assert.Contains("primary", first, StringComparison.Ordinal);

        history.Record(UsageSource.Codex, [Session(11)], Now.AddMinutes(-2));
        Assert.Equal(first, File.ReadAllText(FilePath)); // inside the 30 s write interval

        time.Advance(TimeSpan.FromSeconds(31));
        history.Record(UsageSource.Codex, [Session(12)], Now.AddMinutes(-1));
        Assert.NotEqual(first, File.ReadAllText(FilePath));
        Assert.DoesNotContain("Title", File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_or_hostile_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not valid json");
        var history = new UsageRateHistory(new FakeTimeProvider(Now), FilePath, persist: true);
        Assert.Null(history.Series(UsageSource.Codex, "primary"));

        File.WriteAllText(FilePath, "{\"Windows\":[{\"Source\":99,\"Key\":\"x\",\"Readings\":[[1,2]]},{\"Source\":0,\"Key\":\"\",\"Readings\":[]},{\"Source\":0,\"Key\":\"p\",\"Readings\":[[1],[9999999999999,5]]}]}");
        Assert.Null(new UsageRateHistory(new FakeTimeProvider(Now), FilePath, persist: true).Series(UsageSource.Codex, "p"));
    }
}
