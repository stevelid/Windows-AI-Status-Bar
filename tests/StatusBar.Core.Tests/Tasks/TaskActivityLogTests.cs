using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class TaskActivityLogTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    static AgentTask Task(string id, AgentTaskStatus status = AgentTaskStatus.Working, StateConfidence confidence = StateConfidence.Confirmed, string title = "Sample task") => new()
    {
        Id = id,
        Provider = id.StartsWith("claude", StringComparison.Ordinal) ? AgentProvider.Claude : AgentProvider.Codex,
        Title = title,
        Status = status,
        Confidence = confidence,
        LastActivity = Now,
    };

    [Fact]
    public void A_working_task_opens_an_interval_and_finishing_closes_it()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);

        log.Observe([Task("codex:a")]);
        time.Advance(TimeSpan.FromMinutes(10));
        var open = Assert.Single(log.Intervals(AgentProvider.Codex));
        Assert.Equal(Now, open.Start);
        Assert.Null(open.End);

        log.Observe([Task("codex:a", AgentTaskStatus.Complete)]);
        var closed = Assert.Single(log.Intervals(AgentProvider.Codex));
        Assert.Equal(Now.AddMinutes(10), closed.End);
    }

    [Fact]
    public void Repeated_observations_keep_one_interval_and_a_new_turn_opens_another()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);

        log.Observe([Task("codex:a")]);
        time.Advance(TimeSpan.FromMinutes(1));
        log.Observe([Task("codex:a")]);
        time.Advance(TimeSpan.FromMinutes(1));
        log.Observe([Task("codex:a", AgentTaskStatus.Complete)]);
        time.Advance(TimeSpan.FromMinutes(5));
        log.Observe([Task("codex:a")]);

        var intervals = log.Intervals(AgentProvider.Codex);
        Assert.Equal(2, intervals.Count);
        Assert.Equal(Now.AddMinutes(2), intervals[0].End);
        Assert.Null(intervals[1].End);
    }

    [Theory]
    [InlineData(AgentTaskStatus.NeedsAttention, StateConfidence.Inferred)]
    [InlineData(AgentTaskStatus.Unknown, StateConfidence.Stale)]
    [InlineData(AgentTaskStatus.Working, StateConfidence.Stale)]
    public void Only_a_task_that_is_really_working_counts(AgentTaskStatus status, StateConfidence confidence)
    {
        var log = new TaskActivityLog(new FakeTimeProvider(Now));

        log.Observe([Task("codex:a", status, confidence)]);

        Assert.Empty(log.Intervals(AgentProvider.Codex));
    }

    [Fact]
    public void A_task_that_disappears_is_closed_and_providers_are_kept_apart()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);
        log.Observe([Task("codex:a"), Task("claude:b")]);
        time.Advance(TimeSpan.FromMinutes(3));

        log.Observe([Task("claude:b")]);

        Assert.Equal(Now.AddMinutes(3), Assert.Single(log.Intervals(AgentProvider.Codex)).End);
        Assert.Null(Assert.Single(log.Intervals(AgentProvider.Claude)).End);
    }

    [Fact]
    public void Running_between_lists_each_conversation_once_with_its_latest_title()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);
        log.Observe([Task("codex:a", title: "First name")]);
        time.Advance(TimeSpan.FromMinutes(10));
        log.Observe([Task("codex:a", title: "Renamed")]);
        time.Advance(TimeSpan.FromMinutes(10));
        log.Observe([]);
        time.Advance(TimeSpan.FromMinutes(10));
        log.Observe([Task("codex:a", title: "Renamed")]);
        log.Observe([Task("codex:a", title: "Renamed"), Task("codex:c", title: "Other")]);

        var found = log.RunningBetween(AgentProvider.Codex, Now.AddMinutes(5), Now.AddMinutes(25));
        Assert.Equal("Renamed", Assert.Single(found).Title);

        Assert.Empty(log.RunningBetween(AgentProvider.Codex, Now.AddMinutes(21), Now.AddMinutes(29)));
        Assert.Equal(2, log.RunningBetween(AgentProvider.Codex, Now.AddMinutes(30), Now.AddMinutes(31)).Count);
    }

    [Fact]
    public void Old_intervals_expire_and_the_log_is_capped()
    {
        var time = new FakeTimeProvider(Now);
        var log = new TaskActivityLog(time);
        for (var i = 0; i < TaskActivityLog.MaximumIntervals + 20; i++)
        {
            log.Observe([Task("codex:t" + i)]);
            time.Advance(TimeSpan.FromSeconds(1));
            log.Observe([]);
        }

        Assert.Equal(TaskActivityLog.MaximumIntervals, log.Intervals(AgentProvider.Codex).Count);

        time.Advance(TaskActivityLog.Retention + TimeSpan.FromMinutes(1));
        Assert.Empty(log.Intervals(AgentProvider.Codex));
    }
}
