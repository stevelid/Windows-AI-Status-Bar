using System.Text;
using StatusBar.Core.Claude;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Claude;

public class ClaudeCodeTranscriptParserTests
{
    static readonly DateTimeOffset FallbackTime = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    static readonly DateTimeOffset EvaluationTime = FallbackTime.AddSeconds(5);

    [Theory]
    [InlineData("provisional-turn-running.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-turn-complete.jsonl", AgentTaskStatus.Complete, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-turn-complete-with-question.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Asked you a question", null)]
    [InlineData("provisional-interrupted.jsonl", AgentTaskStatus.Complete, StateConfidence.Confirmed, null, "Stopped")]
    [InlineData("provisional-tool-pending.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-ai-title.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-custom-title.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-sidechain.jsonl", AgentTaskStatus.Unknown, StateConfidence.Stale, null, null)]
    [InlineData("provisional-malformed.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    public void Fixture_maps_to_expected_state_and_limits_titles_to_display_text(
        string fixture,
        AgentTaskStatus expectedStatus,
        StateConfidence expectedConfidence,
        string? expectedReason,
        string? expectedDetail)
    {
        var (state, drift) = ReadFixture(fixture);
        var task = ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectedConfidence, task.Confidence);
        Assert.Equal(expectedReason, task.AttentionReason);
        Assert.Equal(expectedDetail, task.StatusDetail);
        Assert.InRange(task.Title.Length, 1, 48);
        Assert.Equal(fixture == "provisional-malformed.jsonl" ? 1 : 0, drift.MalformedCount);
        if (fixture == "provisional-turn-running.jsonl")
        {
            Assert.Equal("claude:synthetic-session", task.Id);
            Assert.Equal("synthetic-session", task.SessionReference);
            Assert.Equal("Summarise the sample document", task.Title);
        }
        if (fixture == "provisional-ai-title.jsonl") Assert.Equal("Sample summary", task.Title);
        if (fixture == "provisional-custom-title.jsonl") Assert.Equal("Custom selected title", task.Title);
        if (fixture == "provisional-sidechain.jsonl") Assert.True(state.IsSidechainOnly);
    }

    [Fact]
    public void Custom_title_precedes_ai_title_and_first_prompt()
    {
        var (state, _) = ReadFixture("provisional-custom-title.jsonl");

        var task = ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal("Custom selected title", task.Title);
    }

    [Fact]
    public void User_tool_result_resolves_the_matching_pending_tool()
    {
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();
        ClaudeCodeTranscriptParser.Apply(state, UserLine("Check this"), FallbackTime, drift);
        ClaudeCodeTranscriptParser.Apply(
            state,
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-a\",\"name\":\"Read\"}]}}",
            FallbackTime.AddSeconds(1), drift);
        ClaudeCodeTranscriptParser.Apply(
            state,
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-a\"}]}}",
            FallbackTime.AddSeconds(2), drift);

        Assert.Empty(state.PendingTools);
        Assert.Equal(AgentTaskStatus.Working, ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default).Status);
    }

    [Fact]
    public void Slash_command_and_meta_user_records_do_not_start_a_turn_or_set_a_title()
    {
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();
        ClaudeCodeTranscriptParser.Apply(state, UserLine("/review this"), FallbackTime, drift);
        ClaudeCodeTranscriptParser.Apply(
            state,
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Synthetic command text\"}]}}",
            FallbackTime.AddSeconds(1), drift);

        Assert.False(state.HasSeenTurnEvent);
        Assert.Null(state.FirstPromptTitleCandidate);
    }

    [Fact]
    public void Running_turn_becomes_stale_then_unknown_at_the_configured_thresholds()
    {
        var (state, _) = ReadFixture("provisional-turn-running.jsonl");

        var stale = ClaudeCodeTaskMapper.Map(state, state.LastActivity.Add(TaskTimings.Default.ClaudeCodeWorkingStaleAfter), TaskTimings.Default);
        var unknown = ClaudeCodeTaskMapper.Map(state, state.LastActivity.Add(TaskTimings.Default.ClaudeCodeWorkingUnknownAfter), TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.Working, stale.Status);
        Assert.Equal(StateConfidence.Stale, stale.Confidence);
        Assert.Equal(AgentTaskStatus.Unknown, unknown.Status);
        Assert.Equal(StateConfidence.Stale, unknown.Confidence);
    }

    [Fact]
    public void Completed_question_returns_to_complete_at_the_configured_expiry()
    {
        var (state, _) = ReadFixture("provisional-turn-complete-with-question.jsonl");
        var question = ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        var expired = ClaudeCodeTaskMapper.Map(
            state,
            state.LastActivity.Add(TaskTimings.Default.QuestionAttentionExpiry),
            TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.NeedsAttention, question.Status);
        Assert.StartsWith("question:", question.EvidenceKey);
        Assert.Equal(AgentTaskStatus.Complete, expired.Status);
    }

    [Fact]
    public void Reapplying_records_is_idempotent_and_keeps_only_pending_tool_ids()
    {
        var lines = File.ReadAllLines(FixturePath("provisional-tool-pending.jsonl"));
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();
        foreach (var line in lines) ClaudeCodeTranscriptParser.Apply(state, line, FallbackTime, drift);
        var first = ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        foreach (var line in lines) ClaudeCodeTranscriptParser.Apply(state, line, FallbackTime, drift);
        var second = ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal(first, second);
        Assert.Single(state.PendingTools);
        Assert.Equal(["synthetic-tool"], state.PendingTools.Keys);
        Assert.Equal("Check the sample document", state.FirstPromptTitleCandidate);
    }

    [Fact]
    public void Incremental_reader_holds_a_partial_record_until_completed()
    {
        using var file = new TempJsonlFile();
        var reader = new IncrementalJsonlReader(file.Path);
        var line = UserLine("Incremental sample");
        file.Write(line);
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();

        Assert.Empty(reader.ReadNewLines().Lines);
        file.Append("\n");
        foreach (var record in reader.ReadNewLines().Lines)
            ClaudeCodeTranscriptParser.Apply(state, record, FallbackTime, drift);

        Assert.Equal("Incremental sample", state.FirstPromptTitleCandidate);
        Assert.Equal(AgentTaskStatus.Working, ClaudeCodeTaskMapper.Map(state, EvaluationTime, TaskTimings.Default).Status);
    }

    [Fact]
    public void Missing_timestamp_uses_the_supplied_file_time()
    {
        var state = new ClaudeCodeSessionState();
        ClaudeCodeTranscriptParser.Apply(state, UserLine("Timestamp fallback", includeTimestamp: false), FallbackTime, new FormatDriftCounter());

        Assert.Equal(FallbackTime, state.LastActivity);
    }

    static (ClaudeCodeSessionState State, FormatDriftCounter Drift) ReadFixture(string fixture)
    {
        var state = new ClaudeCodeSessionState();
        var drift = new FormatDriftCounter();
        foreach (var line in File.ReadAllLines(FixturePath(fixture)))
            ClaudeCodeTranscriptParser.Apply(state, line, FallbackTime, drift);
        return (state, drift);
    }

    static string UserLine(string text, bool includeTimestamp = true) =>
        "{\"type\":\"user\",\"sessionId\":\"synthetic-session\"," +
        (includeTimestamp ? "\"timestamp\":\"2026-01-01T12:00:00Z\"," : string.Empty) +
        "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" +
        System.Text.Json.JsonSerializer.Serialize(text) + "}]}}";

    static string FixturePath(string fixture) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "claude-code",
        fixture);

    sealed class TempJsonlFile : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "statusbar-claude-" + Guid.NewGuid().ToString("N"));

        internal TempJsonlFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "data.jsonl");
        }

        internal string Path { get; }
        internal void Write(string text) => File.WriteAllText(Path, text);

        internal void Append(string text)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }
}
