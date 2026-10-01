using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Tasks;

public sealed class TaskActivityPersistenceTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    readonly string _directory = Path.Combine(Path.GetTempPath(), "statusbar-activity-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_directory, "activity-history.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    static AgentTask Working(string id, string title = "Made-up placeholder title") => new()
    {
        Id = id,
        Provider = id.StartsWith("claude", StringComparison.Ordinal) ? AgentProvider.Claude : AgentProvider.Codex,
        Title = title,
        Status = AgentTaskStatus.Working,
        Confidence = StateConfidence.Confirmed,
        LastActivity = Now,
    };

    [Fact]
    public void Intervals_round_trip_without_titles()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("codex:aaaa1111")]);
        time.Advance(TimeSpan.FromMinutes(10));
        log.Observe([]);

        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("Made-up placeholder title", text);
        Assert.DoesNotContain("itle", text);

        time.Advance(TimeSpan.FromMinutes(1));
        var reloaded = new TaskActivityLog(time, FilePath, persist: true);
        var interval = Assert.Single(reloaded.Intervals(AgentProvider.Codex));
        Assert.Equal("codex:aaaa1111", interval.TaskId);
        Assert.Equal(Now, interval.Start);
        Assert.Equal(Now.AddMinutes(10), interval.End);
        Assert.Equal("", interval.Title);
    }

    [Fact]
    public void An_interval_open_at_exit_is_closed_at_its_last_seen_time_on_reload()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("claude:bbbb2222")]);
        time.Advance(TimeSpan.FromSeconds(40));
        log.Observe([Working("claude:bbbb2222")]);   // seen again and written (30 s have passed)
        // The app dies here: no further observation, 20 minutes pass.
        time.Advance(TimeSpan.FromMinutes(20));

        var reloaded = new TaskActivityLog(time, FilePath, persist: true);
        var interval = Assert.Single(reloaded.Intervals(AgentProvider.Claude));
        Assert.Equal(Now.AddSeconds(40), interval.End);
    }

    [Fact]
    public void Writes_are_throttled_to_every_thirty_seconds()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("codex:aaaa1111")]);
        var first = File.ReadAllText(FilePath);
        time.Advance(TimeSpan.FromSeconds(10));
        log.Observe([]);

        Assert.Equal(first, File.ReadAllText(FilePath));
        time.Advance(TimeSpan.FromSeconds(25));
        log.Observe([]);
        Assert.NotEqual(first, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Intervals_older_than_the_retention_are_not_loaded()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("codex:old00000")]);
        time.Advance(TimeSpan.FromMinutes(5));
        log.Observe([]);
        time.Advance(TaskActivityLog.Retention + TimeSpan.FromMinutes(1));

        var reloaded = new TaskActivityLog(time, FilePath, persist: true);
        Assert.Empty(reloaded.Intervals(AgentProvider.Codex));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Intervals\":[{\"Id\":\"\",\"Provider\":99,\"Start\":1,\"End\":2}],\"Watched\":[[1]]}")]
    public void A_damaged_file_is_ignored(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);
        var log = new TaskActivityLog(new FakeTimeProvider(Now), FilePath, persist: true);
        Assert.Empty(log.Intervals(AgentProvider.Codex));
        Assert.Empty(log.Intervals(AgentProvider.Claude));
    }

    [Fact]
    public void An_oversized_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, new string(' ', 1024 * 1024 + 10));
        var log = new TaskActivityLog(new FakeTimeProvider(Now), FilePath, persist: true);
        Assert.Empty(log.Intervals(AgentProvider.Codex));
    }

    [Fact]
    public void Turning_persistence_off_deletes_the_file_and_on_writes_it_again()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("codex:aaaa1111")]);
        Assert.True(File.Exists(FilePath));

        log.SetPersistence(false);
        Assert.False(File.Exists(FilePath));
        log.Observe([Working("codex:aaaa1111")]);
        Assert.False(File.Exists(FilePath));

        log.SetPersistence(true);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Without_persistence_nothing_is_written()
    {
        var log = new TaskActivityLog(new FakeTimeProvider(Now), FilePath, persist: false);
        log.Observe([Working("codex:aaaa1111")]);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Watched_time_covers_this_run_and_earlier_runs_but_not_the_gap_between()
    {
        var time = new FakeTimeProvider(Now);
        var first = new TaskActivityLog(time, FilePath, persist: true);
        time.Advance(TimeSpan.FromMinutes(30));
        first.Observe([]);   // watched Now .. Now+30

        time.Advance(TimeSpan.FromMinutes(60));   // app off from +30 to +90
        var second = new TaskActivityLog(time, FilePath, persist: true);
        time.Advance(TimeSpan.FromMinutes(10));

        DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);
        Assert.True(second.WasWatching(At(10), At(16)));    // earlier run
        Assert.False(second.WasWatching(At(40), At(46)));   // app was off
        Assert.True(second.WasWatching(At(92), At(98)));    // this run
        Assert.True(second.WasWatching(At(25), At(35)));    // overlaps the end of the earlier run
    }

    [Fact]
    public void Without_a_file_only_this_run_counts_as_watched()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.False(log.WasWatching(Now.AddMinutes(-60), Now.AddMinutes(-54)));
        Assert.True(log.WasWatching(Now.AddMinutes(5), Now.AddMinutes(11)));
    }

    [Fact]
    public void A_reloaded_interval_still_feeds_the_usage_attribution()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time, FilePath, persist: true);
        log.Observe([Working("codex:aaaa1111")]);
        time.Advance(TimeSpan.FromMinutes(10));
        log.Observe([]);
        time.Advance(TimeSpan.FromMinutes(5));

        var reloaded = new TaskActivityLog(time, FilePath, persist: true);
        var readings = new[] { (Now, 80.0), (Now.AddMinutes(10), 74.0) };
        var result = UsageAttributionCalculator.Compute(readings, reloaded.Intervals(AgentProvider.Codex), time.GetUtcNow(), TaskActivityLog.Retention);

        Assert.Equal(6, result.PointsByTask["codex:aaaa1111"], precision: 6);
        Assert.Equal(0, result.UnattributedPoints);
    }
}
