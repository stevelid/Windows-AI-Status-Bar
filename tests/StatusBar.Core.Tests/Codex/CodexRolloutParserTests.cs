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
    [InlineData("provisional-user-input-async-turn-complete.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Waiting for your answer", null)]
    [InlineData("provisional-user-input-async-answered.jsonl", AgentTaskStatus.Working, StateConfidence.Confirmed, null, null)]
    [InlineData("provisional-user-input-policy-never.jsonl", AgentTaskStatus.NeedsAttention, StateConfidence.Inferred, "Waiting for your input", null)]
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
        if (fixture == "provisional-turn-running.jsonl")
        {
            Assert.Equal("codex:thread-running", task.Id);
            Assert.Equal("thread-running", task.SessionReference);
        }
        if (fixture == "provisional-malformed-and-truncated.jsonl") Assert.Equal(1, drift.MalformedCount);
    }

    [Theory]
    // Real rollouts store arguments as a JSON string; accept an object too in case the format changes.
    [InlineData("\"{\\\"sandbox_permissions\\\":\\\"require_escalated\\\"}\"")]
    [InlineData("{\"sandbox_permissions\":\"require_escalated\"}")]
    public void Escalated_call_is_pending_approval_whether_arguments_are_a_string_or_an_object(string argumentsJson)
    {
        var line = "{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"response_item\",\"payload\":" +
                   "{\"type\":\"function_call\",\"call_id\":\"call-approval\",\"name\":\"exec\",\"arguments\":" +
                   argumentsJson + "}}";
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        CodexRolloutParser.Apply(state, line, FallbackTime, drift);

        Assert.Single(state.PendingCalls);
        Assert.Equal(CodexPendingKind.Approval, state.PendingCalls["call-approval"].Kind);
    }

    [Fact]
    public void Sessions_without_session_meta_get_distinct_ids_from_their_file_names()
    {
        var first = CodexSessionReader.FileKeyFromPath("rollout-2026-09-28T10-00-00-0f8fad5b-d9cb-469f-a165-70867728950e.jsonl");
        var second = CodexSessionReader.FileKeyFromPath("rollout-2026-09-28T10-05-00-7c9e6679-7425-40de-944b-e07fc1f90ae7.jsonl");
        var stateA = new CodexSessionState { FileKey = first };
        var stateB = new CodexSessionState { FileKey = second };

        var idA = CodexTaskMapper.Map(stateA, EvaluationTime, TaskTimings.Default).Id;
        var idB = CodexTaskMapper.Map(stateB, EvaluationTime, TaskTimings.Default).Id;

        Assert.Equal("codex:0f8fad5b-d9cb-469f-a165-70867728950e", idA);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public void Known_activity_records_do_not_count_as_format_drift()
    {
        var (state, drift) = ReadFixture("provisional-known-activity.jsonl");

        Assert.Equal(0, drift.UnknownCount);
        Assert.Equal(0, drift.MalformedCount);
        Assert.Equal(AgentTaskStatus.Working, CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default).Status);
    }

    [Fact]
    public void Pending_call_is_working_at_two_seconds_and_attention_at_four_seconds()
    {
        var (state, _) = ReadFixture("provisional-pending-user-input.jsonl");

        var atTwoSeconds = CodexTaskMapper.Map(state, FallbackTime.AddSeconds(3), TaskTimings.Default);
        var atFourSeconds = CodexTaskMapper.Map(state, FallbackTime.AddSeconds(5), TaskTimings.Default);

        Assert.Equal(AgentTaskStatus.Working, atTwoSeconds.Status);
        Assert.Equal(AgentTaskStatus.NeedsAttention, atFourSeconds.Status);
        Assert.Equal("pending-call:call-input", atFourSeconds.EvidenceKey);
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
    public void Real_shape_spawned_subagent_records_its_parent()
    {
        var (state, _) = ReadFixture("provisional-subagent-spawned.jsonl");

        Assert.Equal("sub-agent", state.Source);
        Assert.Equal("thread-parent", state.ParentThreadId);
        Assert.False(state.IsGuardianReview);
    }

    [Fact]
    public void Guardian_review_session_is_recognised_and_is_not_a_subagent_of_anything()
    {
        var (state, _) = ReadFixture("provisional-guardian-review.jsonl");

        Assert.True(state.IsGuardianReview);
        Assert.Null(state.ParentThreadId);
        Assert.Equal("thread-guardian", state.ThreadId);
    }

    [Fact]
    public void Ordinary_desktop_session_is_not_a_guardian_review()
    {
        var (state, _) = ReadFixture("provisional-turn-running.jsonl");

        Assert.False(state.IsGuardianReview);
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

    [Fact]
    public void Opening_an_old_thread_does_not_make_it_look_just_finished()
    {
        // The turn ended at 10:00:05 with a question; the app wrote housekeeping records at 12:00
        // when Steve opened the thread. Evaluated at 12:00:05.
        var (state, _) = ReadFixture("provisional-reopened-completed-thread.jsonl");
        var endedAt = DateTimeOffset.Parse("2026-01-01T10:00:05Z");

        var task = CodexTaskMapper.Map(state, EvaluationTime, TaskTimings.Default);

        // Dated by the turn end, so it stays out of "recently completed" and keeps its old evidence
        // key (no second pop-up for the same question).
        Assert.Equal(endedAt, task.LastActivity);
        Assert.Equal("question:" + endedAt.UtcTicks, task.EvidenceKey);

        var muchLater = CodexTaskMapper.Map(state, endedAt + TaskTimings.Default.QuestionAttentionExpiry, TaskTimings.Default);
        Assert.Equal(AgentTaskStatus.Complete, muchLater.Status);
        Assert.Equal(endedAt, muchLater.LastActivity);
    }

    static (CodexSessionState State, FormatDriftCounter Drift) ReadFixture(string fixture)
    {
        var state = new CodexSessionState();
        var drift = new FormatDriftCounter();
        var lines = File.ReadAllLines(FixturePath(fixture));
        foreach (var line in lines)
            CodexRolloutParser.Apply(state, line, FallbackTime, drift);
        return (state, drift);
    }

    static string FixturePath(string fixture) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "codex",
        fixture);
}
