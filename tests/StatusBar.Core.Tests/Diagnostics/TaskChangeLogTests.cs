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

        Assert.Contains("Task codex:0f8fad5b Working/Confirmed -> NeedsAttention/Confirmed (Asked you a question)", lines);
        Assert.Contains("Task codex:11111111 added: Working/Confirmed", lines);
        Assert.Contains("Task codex:7c9e6679 removed (was Complete)", lines);
        Assert.Contains("Counts: working 1 -> 1, attention 0 -> 1", lines);
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
