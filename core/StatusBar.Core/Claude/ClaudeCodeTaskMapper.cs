using System.Globalization;
using StatusBar.Core.Common;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Claude;

/// <summary>Maps accumulated Claude Code transcript state to the provider-neutral task model.</summary>
internal static class ClaudeCodeTaskMapper
{
    /// <summary>Detail for a finished turn whose background agents or shells are still running.</summary>
    internal const string WaitingForBackgroundDetail = "Waiting for background agents";

    internal static AgentTask Map(
        ClaudeCodeSessionState state,
        DateTimeOffset now,
        TaskTimings timings,
        ClaudeSubagentActivity? subagents = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timings);

        var sessionId = string.IsNullOrWhiteSpace(state.SessionId) ? "unknown" : state.SessionId;
        var title = state.CustomTitleCandidate ?? state.AiTitleCandidate ?? state.FirstPromptTitleCandidate ??
            (state.CwdLeaf is { Length: > 0 } leaf
                ? TextSanitizer.SanitizeTitleCandidate("Claude · " + leaf) ?? "Claude task"
                : "Claude task");
        var task = new AgentTask
        {
            Id = "claude:" + sessionId,
            Provider = AgentProvider.Claude,
            Title = title,
            Status = AgentTaskStatus.Unknown,
            Confidence = StateConfidence.Stale,
            LastActivity = state.HasActivity ? state.LastActivity : DateTimeOffset.MinValue,
            SessionReference = state.SessionId,
        };

        if (!state.HasSeenTurnEvent) return task;

        // Subagents write their own transcripts, so their writes count as this session's activity.
        var effectiveActivity = subagents is not null && subagents.LastActivity > task.LastActivity
            ? subagents.LastActivity
            : task.LastActivity;
        var inactivity = now >= effectiveActivity ? now - effectiveActivity : TimeSpan.Zero;
        if (state.Turn == ClaudeCodeTurnStatus.Running)
        {
            task = task with { LastActivity = effectiveActivity };
            // ⚠️ A-K5 A pending AskUserQuestion/ExitPlanMode waits for Steve, however long it has been.
            // Ordinary pending tools stay Working: a running command and a permission prompt look the
            // same in the transcript (the opt-in hooks tell them apart).
            var waiting = state.PendingTools.Values
                .Where(tool => tool.Kind != ClaudePendingToolKind.Other)
                .OrderBy(tool => tool.Since)
                .FirstOrDefault();
            if (waiting is not null)
            {
                return task with
                {
                    Status = AgentTaskStatus.NeedsAttention,
                    Confidence = StateConfidence.Inferred,
                    AttentionReason = waiting.Kind == ClaudePendingToolKind.Question
                        ? "Waiting for your answer"
                        : "Plan needs approval",
                    EvidenceKey = "pending-tool:" + waiting.ToolUseId,
                };
            }

            if (inactivity >= timings.ClaudeCodeWorkingUnknownAfter)
                return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale };
            if (inactivity >= timings.ClaudeCodeWorkingStaleAfter)
                return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Stale };
            return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed };
        }

        // ⚠️ A-K6 The turn has ended but launched background work has not reported back (or a subagent
        // transcript is still being written): Claude is waiting on it, not finished.
        // Optional AI check: once it has answered for this turn it decides whether a finished turn asked a
        // question; while its answer is awaited the turn is shown quietly, and the rules are the fallback.
        var asksUser = StatusBar.Core.Judgment.TurnVerdictPolicy.AsksUser(
            state.EndedWithQuestion, state.FinalMessageAt, state.Verdict, now) == true;
        var waitingOnBackground = state.Turn == ClaudeCodeTurnStatus.Completed &&
            !(asksUser && inactivity < timings.QuestionAttentionExpiry) &&
            inactivity < timings.ClaudeCodeWorkingUnknownAfter &&
            (subagents?.Running == true ||
             state.BackgroundTasks.Values.Any(since => now - since < timings.ClaudeCodeWorkingUnknownAfter));
        if (waitingOnBackground)
        {
            return task with
            {
                Status = AgentTaskStatus.Working,
                Confidence = inactivity >= timings.ClaudeCodeWorkingStaleAfter ? StateConfidence.Stale : StateConfidence.Inferred,
                StatusDetail = WaitingForBackgroundDetail,
                LastActivity = effectiveActivity,
            };
        }

        // A finished turn is dated by when it ended, so records written later without a new turn
        // (titles, opening the session) do not re-show it as just finished (see CodexTaskMapper).
        if (state.TurnEndedAt is { } endedAt)
        {
            task = task with { LastActivity = endedAt };
            inactivity = now >= endedAt ? now - endedAt : TimeSpan.Zero;
        }

        return state.Turn switch
        {
            ClaudeCodeTurnStatus.Completed when asksUser && inactivity < timings.QuestionAttentionExpiry => task with
            {
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = StateConfidence.Inferred,
                AttentionReason = "Asked you a question",
                EvidenceKey = "question:" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            },
            ClaudeCodeTurnStatus.Completed => task with
            {
                Status = AgentTaskStatus.Complete,
                Confidence = StateConfidence.Confirmed,
                StatusDetail = StatusBar.Core.Judgment.TurnVerdictPolicy.Detail(state.FinalMessageAt, state.Verdict),
            },
            ClaudeCodeTurnStatus.Aborted => task with
            {
                Status = AgentTaskStatus.Complete,
                Confidence = StateConfidence.Confirmed,
                StatusDetail = "Stopped",
            },
            _ => task,
        };
    }
}
