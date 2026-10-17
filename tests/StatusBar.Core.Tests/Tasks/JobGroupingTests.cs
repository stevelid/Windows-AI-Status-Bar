using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public class JobGroupingTests
{
    [Theory]
    [InlineData("6101 Example Garden Centre", "6101")]
    [InlineData("Quote for 6102 extension", "6102")]
    [InlineData("Review 5863.L1 changes", "5863")]
    [InlineData("5882 Example House - S73 appeal", "5882")]
    [InlineData("2026 plan for 6103", "6103")]
    [InlineData("2026 plan", null)]
    [InlineData("Update to 3.0.1234 release", null)]
    [InlineData("Order 12345 follow up", null)]
    [InlineData("Programming folder UX review", null)]
    [InlineData("", null)]
    public void JobNumber_extracts_the_first_standalone_four_digit_token(string title, string? expected) =>
        Assert.Equal(expected, JobGrouping.JobNumber(title));

    static AgentTask Task(string id, string title) => new()
    {
        Id = id,
        Provider = AgentProvider.Claude,
        Title = title,
        Status = AgentTaskStatus.Working,
        Confidence = StateConfidence.Confirmed,
        LastActivity = DateTimeOffset.UnixEpoch,
    };

    static string[] Ids(IEnumerable<TaskCluster> clusters) =>
        clusters.SelectMany(c => c.Tasks).Select(t => t.Id).ToArray();

    [Fact]
    public void Cluster_without_shared_numbers_keeps_the_order()
    {
        var tasks = new[] { Task("a", "6101 One"), Task("b", "Two"), Task("c", "6102 Three") };
        var clusters = JobGrouping.Cluster(tasks);
        Assert.Equal(["a", "b", "c"], Ids(clusters));
        Assert.All(clusters, c => Assert.Null(c.JobNumber));
    }

    [Fact]
    public void Cluster_pulls_a_shared_pair_to_the_first_position()
    {
        var tasks = new[] { Task("a", "6101 One"), Task("b", "Other"), Task("c", "Review 6101.L1") };
        var clusters = JobGrouping.Cluster(tasks);
        Assert.Equal(2, clusters.Count);
        Assert.Equal("6101", clusters[0].JobNumber);
        Assert.Equal(["a", "c"], clusters[0].Tasks.Select(t => t.Id));
        Assert.Null(clusters[1].JobNumber);
        Assert.Equal("b", clusters[1].Tasks[0].Id);
    }

    [Fact]
    public void Cluster_leaves_a_single_job_numbered_task_ungrouped()
    {
        var clusters = JobGrouping.Cluster([Task("a", "6101 One")]);
        Assert.Null(Assert.Single(clusters).JobNumber);
    }

    [Fact]
    public void Cluster_builds_two_separate_groups_in_first_seen_order()
    {
        var tasks = new[]
        {
            Task("a", "6102 A"), Task("b", "6101 B"), Task("c", "6102 C"), Task("d", "6101 D"), Task("e", "Solo"),
        };
        var clusters = JobGrouping.Cluster(tasks);
        Assert.Equal(["6102", "6101", null], clusters.Select(c => c.JobNumber));
        Assert.Equal(["a", "c", "b", "d", "e"], Ids(clusters));
    }

    [Fact]
    public void Cluster_is_stable_for_repeated_calls()
    {
        var tasks = new[] { Task("a", "6101 A"), Task("b", "x"), Task("c", "6101 C"), Task("d", "6101 D") };
        Assert.Equal(Ids(JobGrouping.Cluster(tasks)), Ids(JobGrouping.Cluster(tasks)));
        Assert.Equal(["a", "c", "d", "b"], Ids(JobGrouping.Cluster(tasks)));
    }
}
