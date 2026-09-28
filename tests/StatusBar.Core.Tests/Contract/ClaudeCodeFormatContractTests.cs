using StatusBar.Core.Claude;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Contract;

public class ClaudeCodeFormatContractTests
{
    static readonly DateTimeOffset FallbackTime = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    [Theory]
    [InlineData("provisional-turn-running.jsonl", AgentTaskStatus.Working)]
    [InlineData("provisional-turn-complete.jsonl", AgentTaskStatus.Complete)]
    [InlineData("provisional-turn-complete-with-question.jsonl", AgentTaskStatus.NeedsAttention)]
    [InlineData("provisional-interrupted.jsonl", AgentTaskStatus.Complete)]
    [InlineData("provisional-tool-pending.jsonl", AgentTaskStatus.Working)]
    [InlineData("provisional-ai-title.jsonl", AgentTaskStatus.Working)]
    [InlineData("provisional-custom-title.jsonl", AgentTaskStatus.Working)]
    [InlineData("provisional-sidechain.jsonl", AgentTaskStatus.Unknown)]
    [InlineData("provisional-malformed.jsonl", AgentTaskStatus.Working)]
    public void Provisional_transcript_contract_maps_each_fixture(string fixture, AgentTaskStatus expectedStatus)
    {
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code", fixture);
        foreach (var line in File.ReadAllLines(fixturePath))
            ClaudeCodeTranscriptParser.Apply(state, line, FallbackTime, drift);

        var task = ClaudeCodeTaskMapper.Map(state, FallbackTime.AddSeconds(5), TaskTimings.Default);

        Assert.Equal(expectedStatus, task.Status);
        Assert.DoesNotContain("synthetic sub-agent activity", task.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^claude:[A-Za-z0-9._-]+$", task.Id);
    }
}
