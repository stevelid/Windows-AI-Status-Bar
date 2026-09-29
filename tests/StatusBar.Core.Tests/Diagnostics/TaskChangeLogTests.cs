using StatusBar.Core.Diagnostics;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Diagnostics;

public class TaskChangeLogTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static AgentTask Task(string id, AgentTaskStatus status, string title = "6595 quote review", string? reason = null) => new()
    {
        Id = id,
        Provider = AgentProvider.Codex,
        Title = title,
        Status = status,
        Confidence = StateConfidence.Confirmed,
        LastActivity = Now,
        AttentionReason = reason,
    };

    static StatusBarState State(params AgentTask[] tasks) => new(
        tasks,
        tasks.Count(t => t.Status == AgentTaskStatus.Working),
        tasks.Count(t => t.Status == AgentTaskStatus.NeedsAttention),
        new Dictionary<AgentProvider, ProviderHealth>
        {
            [AgentProvider.Codex] = new(ProviderHealthState.Ok, "Ok", Now),
        });

    [Fact]
    public void Describes_added_changed_and_removed_tasks_with_counts()
    {
        var previous = State(
            Task("codex:0f8fad5b-d9cb-469f-a165-70867728950e", AgentTaskStatus.Working),
            Task("codex:7c9e6679-7425-40de-944b-e07fc1f90ae7", AgentTaskStatus.Complete));
        var next = State(
            Task("codex:0f8fad5b-d9cb-469f-a165-70867728950e", AgentTaskStatus.NeedsAttention, reason: "Asked you a question"),
            Task("codex:11111111-2222-3333-4444-555555555555", AgentTaskStatus.Working));

        var lines = TaskChangeLog.Describe(previous, next);

        Assert.Contains("Task codex:7728950e Working/Confirmed -> NeedsAttention/Confirmed (Asked you a question)", lines);
        Assert.Contains("Task codex:55555555 added: Working/Confirmed", lines);
        Assert.Contains("Task codex:c1f90ae7 removed (was Complete)", lines);
        Assert.Contains("Counts: working 1 -> 1, attention 0 -> 1, done 1 -> 0, failed 0 -> 0", lines);
    }

    [Fact]
    public void Short_ids_use_the_random_tail_so_uuidv7_sessions_stay_distinct()
    {
        // UUIDv7 ids created a few seconds apart share their leading timestamp characters.
        var previous = StatusBarState.Empty;
        var next = State(
            Task("codex:01a0dd2c-1111-7000-8000-00000000aaaa", AgentTaskStatus.Working),
            Task("codex:01a0dd2c-2222-7000-8000-00000000bbbb", AgentTaskStatus.Working));

        var lines = TaskChangeLog.Describe(previous, next);

        Assert.Contains("Task codex:0000aaaa added: Working/Confirmed", lines);
        Assert.Contains("Task codex:0000bbbb added: Working/Confirmed", lines);
    }

    [Fact]
    public void Finishing_a_task_is_reported_in_the_done_count()
    {
        var id = "codex:0f8fad5b-d9cb-469f-a165-70867728950e";
        var lines = TaskChangeLog.Describe(
            State(Task(id, AgentTaskStatus.Working)),
            State(Task(id, AgentTaskStatus.Complete)));

        Assert.Contains("Counts: working 1 -> 0, attention 0 -> 0, done 0 -> 1, failed 0 -> 0", lines);
    }

    [Fact]
    public void Never_includes_task_titles()
    {
        var lines = TaskChangeLog.Describe(
            StatusBarState.Empty,
            State(Task("codex:0f8fad5b", AgentTaskStatus.Working, title: "Secret client report")));

        Assert.DoesNotContain(lines, line => line.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Unchanged_state_produces_no_lines()
    {
        var state = State(Task("codex:0f8fad5b", AgentTaskStatus.Working));

        Assert.Empty(TaskChangeLog.Describe(state, state));
    }
}
