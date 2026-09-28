using StatusBar.Core.Codex;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Codex;

public class CodexRolloutParserTests
{
    static readonly DateTimeOffset FallbackTime = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
    static readonly DateTimeOffset EvaluationTime = FallbackTime.AddSeconds(5);

    [Theory]
    [InlineData("provisional-turn-running.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-turn-complete.jsonl", AgentTaskStatus.Complete, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-turn-aborted.jsonl", AgentTaskStatus.Complete, StateConfidence.Confirmed, null, "Stopped")]
    [InlineData("provisional-pending-escalated-approval.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Waiting for approval", null)]
    [InlineData("provisional-pending-user-input.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Waiting for your input", null)]
    [InlineData("provisional-approval-resolved.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-approval-policy-never.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-malformed-and-truncated.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-paginated-turn.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-turn-complete-with-question.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Asked you a question", null)]
    [InlineData("provisional-turn-complete-with-error.jsonl", AgentTaskStatus.Failed, StateConfidence.Confirmed, null, null)]
    public void Fixture_maps_to_the_expected_task_state(
        string fixture,
        AgentTaskStatus expectedStatus,
        StateConfidence expectedConfidence,
        string? expectedReason,
        string? expectedDetail)
    {
        var (state, drift) = ReadFixture(fixture);

        var task = CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectedConfidence, task.Confidence);
        Assert.Equal(expectedReason, task.AttentionReason);
        Assert.Equal(expectedDetail, task.StatusDetail);
        if (fixture == "provisional-turn-running.jsonl") Assert.Equal("codex:thread-running", task.Id);
        if (fixture == "provisional-malformed-and-truncated.jsonl") Assert.Equal(1, drift.MalformedCount);
    }

    [Fact]
    public void Approval_fixture_only_uses_the_permission_marker_at_test_time()
    {
        var line = AddApprovalMarker(File.ReadAllLines(FixturePath("provisional-pending-escalated-approval.jsonl"))[1]);
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        CodexRolloutParser.Apply(state, line, FallbackTime, drift);

        Assert.Single(state.PendingCalls);
        Assert.Equal(CodexPendingKind.Approval, state.PendingCalls["call-approval"].Kind);
    }

    [Fact]
    public void Pending_call_is_working_at_two_seconds_and_attention_at_four_seconds()
    {
        var (state, _) = ReadFixture("provisional-pending-user-input.jsonl");

        var atTwoSeconds = CodexTaskMapper.Map(state, FallbackTime.AddSeconds(3), TaskTimings.Default);
        var atFourSeconds = CodexTaskMapper.Map(state, FallbackTime.AddSeconds(5), TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.Working, atTwoSeconds.Status);
        Assert.Equal(AgentTaskStatus.NeedsAttention, atFourSeconds.Status);
    }

    [Fact]
    public void Running_turn_becomes_stale_then_unknown_at_the_configured_thresholds()
    {
        var (state, _) = ReadFixture("provisional-turn-running.jsonl");

        var stale = CodexTaskMapper.Map(state, FallbackTime.Add(TaskTimings.Default.CodexWorkingStaleAfter), TaskTimings.Default);
        var unknown = CodexTaskMapper.Map(state, FallbackTime.Add(TaskTimings.Default.CodexWorkingUnknownAfter), TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.Working, stale.Status);
        Assert.Equal(StateConfidence.Stale, stale.Confidence);
        Assert.Equal(AgentTaskStatus.Unknown, unknown.Status);
        Assert.Equal(StateConfidence.Stale, unknown.Confidence);
    }

    [Fact]
    public void Unknown_record_type_increments_type_counter_and_is_ignored()
    {
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        CodexRolloutParser.Apply(state, "{\"type\":\"future_event\",\"payload\":{\"type\":\"new_kind\"}}", FallbackTime, drift);

        Assert.Equal(1, drift.UnknownCount);
        Assert.Equal(1, drift.UnknownBySignature["future_event/new_kind"]);
        Assert.Equal(AgentTaskStatus.Unknown, CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default).Status);
    }

    [Fact]
    public void Unknown_type_names_that_are_not_identifiers_are_replaced_with_other()
    {
        var drift = new FormatDriftCounter();
        CodexRolloutParser.Apply(new CodexSessionState(), "{\"type\":\"private value\",\"payload\":{\"type\":\"unexpected kind\"}}", FallbackTime, drift);

        Assert.Contains("other/other", drift.UnknownBySignature.Keys);
        Assert.DoesNotContain("private", string.Join(' ', drift.UnknownBySignature.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void Reapplying_the_same_lines_is_idempotent()
    {
        var lines = File.ReadAllLines(FixturePath("provisional-pending-user-input.jsonl"));
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        foreach (var line in lines) CodexRolloutParser.Apply(state, line, FallbackTime, drift);
        var first = CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        foreach (var line in lines) CodexRolloutParser.Apply(state, line, FallbackTime, drift);
        var second = CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal(first, second);
        Assert.Single(state.PendingCalls);
    }

    [Fact]
    public void Subagent_fixture_records_parent_and_maps_a_safe_fallback_title()
    {
        var (state, _) = ReadFixture("provisional-subagent-child.jsonl");
        var task = CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        Assert.Equal("thread-child", state.ThreadId);
        Assert.Equal("thread-parent", state.ParentThreadId);
        Assert.Equal("Codex sub-task", task.Title);
    }

    [Fact]
    public void Missing_timestamp_uses_the_supplied_file_time()
    {
        var state = new CodexSessionState();
        CodexRolloutParser.Apply(state, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}", FallbackTime, new FormatDriftCounter());

        Assert.Equal(FallbackTime, state.LastActivity);
    }

    [Fact]
    public void Completed_question_returns_to_complete_at_expiry()
    {
        var (state, _) = ReadFixture("provisional-turn-complete-with-question.jsonl");
        var expiredAt = state.LastActivity.Add(TaskTimings.Default.QuestionAttentionExpiry);

        var task = CodexTaskMapper.Map(state, expiredAt, TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.Complete, task.Status);
    }

    static (CodexSessionState State, FormatDriftCounter Drift) ReadFixture(string fixture)
    {
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        var lines = File.ReadAllLines(FixturePath(fixture));
        foreach (var line in lines)
        {
            var applied = fixture switch
            {
                "provisional-pending-escalated-approval.jsonl" or "provisional-approval-resolved.jsonl"
                    when line.Contains("\"name\":\"exec\"", StringComparison.Ordinal) => AddApprovalMarker(line),
                "provisional-turn-complete-with-question.jsonl"
                    when line.Contains("\"type\":\"task_complete\"", StringComparison.Ordinal) =>
                        line.Replace("\"type\":\"task_complete\"", "\"type\":\"task_complete\",\"last_agent_message\":\"?\"", StringComparison.Ordinal),
                _ => line,
            };
            CodexRolloutParser.Apply(state, applied, FallbackTime, drift);
        }
        return (state, drift);
    }

    static string AddApprovalMarker(string line) =>
        line.Replace("\"name\":\"exec\"", "\"name\":\"exec\",\"arguments\":{\"sandbox_permissions\":\"require_escalated\"}", StringComparison.Ordinal);

    static string FixturePath(string fixture) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "codex",
        fixture);
}
