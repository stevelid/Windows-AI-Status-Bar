using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class TaskHistoryTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    readonly string _directory = Path.Combine(Path.GetTempPath(), "statusbar-history-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_directory, "history.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Records_finished_tasks_newest_first_and_ignores_unfinished_ones()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);

        history.Observe(
        [
            Task("codex:old", AgentTaskStatus.Complete, Now.AddMinutes(-30)),
            Task("codex:new", AgentTaskStatus.Failed, Now.AddMinutes(-5)),
            Task("claude:working", AgentTaskStatus.Working, Now),
            Task("claude:asking", AgentTaskStatus.NeedsAttention, Now),
            Task("claude:unknown", AgentTaskStatus.Unknown, Now),
        ]);

        Assert.Collection(
            history.Entries,
            entry => { Assert.Equal("codex:new", entry.TaskId); Assert.Equal(HistoryOutcome.Failed, entry.Outcome); },
            entry => { Assert.Equal("codex:old", entry.TaskId); Assert.Equal(HistoryOutcome.Complete, entry.Outcome); });
    }

    [Fact]
    public void A_stopped_turn_is_recorded_as_stopped()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);

        history.Observe([Task("claude:a", AgentTaskStatus.Complete, Now, detail: "Stopped")]);

        Assert.Equal(HistoryOutcome.Stopped, Assert.Single(history.Entries).Outcome);
    }

    [Fact]
    public void A_later_finish_replaces_the_earlier_one_and_an_older_one_does_not()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);
        history.Observe([Task("codex:a", AgentTaskStatus.Complete, Now.AddMinutes(-20), title: "First")]);
        history.Observe([Task("codex:a", AgentTaskStatus.Failed, Now.AddMinutes(-5), title: "Second")]);
        history.Observe([Task("codex:a", AgentTaskStatus.Complete, Now.AddMinutes(-40), title: "Stale")]);

        var entry = Assert.Single(history.Entries);
        Assert.Equal("Second", entry.Title);
        Assert.Equal(HistoryOutcome.Failed, entry.Outcome);
    }

    [Fact]
    public void Entries_expire_after_the_retention_period_and_are_capped()
    {
        var time = new FakeTimeProvider(Now);
        var history = new TaskHistory(time, FilePath);
        history.Observe([Task("codex:old", AgentTaskStatus.Complete, Now)]);
        history.Observe(Enumerable.Range(0, TaskHistory.MaximumEntries + 20)
            .Select(i => Task("codex:t" + i, AgentTaskStatus.Complete, Now.AddMinutes(-i - 1))));

        Assert.Equal(TaskHistory.MaximumEntries, history.Entries.Count);
        Assert.DoesNotContain(history.Entries, entry => entry.TaskId == "codex:t" + (TaskHistory.MaximumEntries + 19));

        time.Advance(TaskHistory.Retention + TimeSpan.FromMinutes(1));
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Already_expired_finishes_are_not_recorded()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);

        history.Observe([Task("codex:ancient", AgentTaskStatus.Complete, Now - TaskHistory.Retention - TimeSpan.FromHours(1))]);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Remove_and_clear_raise_changed_only_when_something_changes()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);
        var raised = 0;
        history.Changed += () => raised++;
        history.Observe([Task("codex:a", AgentTaskStatus.Complete, Now), Task("codex:b", AgentTaskStatus.Complete, Now)]);
        Assert.Equal(1, raised);

        history.Remove("codex:missing");
        Assert.Equal(1, raised);
        history.Remove("codex:a");
        Assert.Equal(2, raised);
        history.Clear();
        Assert.Equal(3, raised);
        history.Clear();
        Assert.Equal(3, raised);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Nothing_is_written_to_disk_unless_persistence_is_on()
    {
        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath);

        history.Observe([Task("codex:a", AgentTaskStatus.Complete, Now)]);

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Persisted_history_survives_a_restart_and_turning_persistence_off_deletes_the_file()
    {
        var time = new FakeTimeProvider(Now);
        var first = new TaskHistory(time, FilePath, persist: true);
        first.Observe([Task("codex:a", AgentTaskStatus.Complete, Now.AddMinutes(-3), title: "Sample title", reference: "0199a000-0000-7000-8000-000000000001")]);
        Assert.True(File.Exists(FilePath));

        var second = new TaskHistory(time, FilePath, persist: true);
        var entry = Assert.Single(second.Entries);
        Assert.Equal("Sample title", entry.Title);
        Assert.Equal("0199a000-0000-7000-8000-000000000001", entry.SessionReference);
        Assert.Equal(AgentProvider.Codex, entry.Provider);

        second.SetPersistence(false);
        Assert.False(File.Exists(FilePath));
        Assert.Single(second.Entries);

        second.SetPersistence(true);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void A_damaged_history_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not valid json");

        var history = new TaskHistory(new FakeTimeProvider(Now), FilePath, persist: true);

        Assert.Empty(history.Entries);
        history.Observe([Task("codex:a", AgentTaskStatus.Complete, Now)]);
        Assert.Single(new TaskHistory(new FakeTimeProvider(Now), FilePath, persist: true).Entries);
    }

    [Fact]
    public void Entry_converts_to_a_task_that_navigation_can_open()
    {
        var entry = new HistoryEntry("codex:a", AgentProvider.Codex, "Sample", HistoryOutcome.Failed, Now, "0199a000-0000-7000-8000-000000000001");

        var task = entry.ToTask();

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.NotNull(TaskNavigation.CreateLink(task));
    }

    static AgentTask Task(
        string id,
        AgentTaskStatus status,
        DateTimeOffset lastActivity,
        string title = "Sample task",
        string? detail = null,
        string? reference = null) => new()
    {
        Id = id,
        Provider = id.StartsWith("claude", StringComparison.Ordinal) ? AgentProvider.Claude : AgentProvider.Codex,
        Title = title,
        Status = status,
        Confidence = StateConfidence.Confirmed,
        LastActivity = lastActivity,
        StatusDetail = detail,
        SessionReference = reference,
    };
}
