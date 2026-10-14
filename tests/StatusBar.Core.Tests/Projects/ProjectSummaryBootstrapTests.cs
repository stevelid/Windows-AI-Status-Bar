using StatusBar.Core.Projects;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectSummaryBootstrapTests
{
    [Fact]
    public void Startup_includes_yesterdays_work_and_is_bounded_without_future_or_old_turns()
    {
        var now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");
        var times = Enumerable.Range(0, 15).Select(i => now.AddHours(-i - 1))
            .Append(now.AddDays(-8)).Append(now.AddDays(1)).ToArray();
        var selected = ProjectSummaryBootstrap.Select(times, now);
        Assert.Equal(ProjectSummaryBootstrap.MaximumThreadsPerProvider, selected.Count);
        Assert.All(selected, i => Assert.InRange(now - times[i], TimeSpan.Zero, ProjectSummaryBootstrap.Window));
        Assert.Equal(Enumerable.Range(0, 10), selected);
        Assert.Single(ProjectSummaryBootstrap.Select([now.AddDays(-2)], now));
    }

    [Fact]
    public void Earlier_unattributed_v1_checkpoint_can_be_repaired_once_with_enriched_context()
    {
        var now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");
        var request = new ProjectSummaryRequest("codex:thread", "turn", "5670 Example Arms", null, "Updated report", [], OccurredAt: now);
        var initial = new ProjectOverviewDocument { Coverage = [new(null, "codex:thread", "turn", now, "1", "project-overview-v1", "gpt-6-luna")] };
        var result = new ProjectSummaryResult("5670 Example Arms", "Updated report", [], ["turn:assistant"], "claude-haiku-test");
        var repaired = ProjectSummaryReducer.Apply(initial, request, result, TimeProvider.System);
        Assert.Single(repaired.Projects);
        Assert.Single(repaired.Updates);
        Assert.Equal("claude-haiku-test", Assert.Single(repaired.Coverage).Model);
        Assert.Equal(repaired, ProjectSummaryReducer.Apply(repaired, request, result, TimeProvider.System));
    }
}
