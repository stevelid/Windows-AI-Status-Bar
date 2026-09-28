using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public class AgentTaskTests
{
    [Fact]
    public void With_expression_produces_changed_copy_and_leaves_original_untouched()
    {
        var original = new AgentTask
        {
            Id = "codex:abc",
            Provider = AgentProvider.Codex,
            Title = "6585 report",
            Status = AgentTaskStatus.Working,
            Confidence = StateConfidence.Confirmed,
            LastActivity = DateTimeOffset.UnixEpoch,
        };

        var changed = original with { Status = AgentTaskStatus.NeedsAttention, AttentionReason = "Approval requested" };

        Assert.Equal(AgentTaskStatus.Working, original.Status);
        Assert.Equal(AgentTaskStatus.NeedsAttention, changed.Status);
        Assert.NotEqual(original, changed);
    }
}
