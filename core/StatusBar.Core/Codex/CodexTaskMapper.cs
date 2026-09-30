using System.Globalization;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Maps accumulated Codex rollout state to the provider-neutral task model.</summary>
internal static class CodexTaskMapper
{
    internal static AgentTask Map(CodexSessionState state, DateTimeOffset now, TaskTimings timings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timings);

        var key = !string.IsNullOrWhiteSpace(state.ThreadId) ? state.ThreadId
            : !string.IsNullOrWhiteSpace(state.FileKey) ? state.FileKey
            : "unknown";
        var id = "codex:" + key;
        var title = state.TitleCandidate ?? (IsSubAgent(state.Source)
            ? "Codex sub-task"
            : state.CwdLeaf is { Length: > 0 } leaf ? "Codex · " + leaf : "Codex task");
        var task = new AgentTask
        {
            Id = id,
            Provider = AgentProvider.Codex,
            Title = title,
            Status = AgentTaskStatus.Unknown,
            Confidence = StateConfidence.Stale,
            LastActivity = state.HasActivity ? state.LastActivity : DateTimeOffset.MinValue,
            SessionReference = state.ThreadId,
        };

        if (!state.HasSeenTurnEvent)
            return task;

        var inactivity = now >= task.LastActivity ? now - task.LastActivity : TimeSpan.Zero;
        if (state.Turn == CodexTurnStatus.Running)
        {
            // With approval_policy "never" Codex cannot be waiting for an approval, but a structured
            // question (request_user_input) still waits for Steve, so only approvals are filtered out.
            var approvalsPossible = !string.Equals(state.ApprovalPolicy, "never", StringComparison.OrdinalIgnoreCase);
            var pending = state.PendingCalls.Values
                .Where(call => call.Kind == CodexPendingKind.Input || approvalsPossible)
                .Where(call => now >= call.Since && now - call.Since >= timings.CodexApprovalDebounce)
                .OrderBy(call => call.Since)
                .FirstOrDefault();
            if (pending is not null)
            {
                return task with
                {
                    Status = AgentTaskStatus.NeedsAttention,
                    Confidence = StateConfidence.Inferred,
                    AttentionReason = pending.Kind == CodexPendingKind.Input
                        ? "Waiting for your input"
                        : "Waiting for approval",
                    EvidenceKey = "pending-call:" + pending.CallId,
                };
            }

            if (inactivity >= timings.CodexWorkingUnknownAfter)
            {
                return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale };
            }
            if (inactivity >= timings.CodexWorkingStaleAfter)
            {
                return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Stale };
            }
            return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed };
        }

        // A finished turn is dated by when it ended, so later housekeeping records (such as those
        // written when Steve opens an old thread) neither re-show it as just finished nor re-raise
        // its question with a new evidence key.
        if (state.TurnEndedAt is { } endedAt)
        {
            task = task with { LastActivity = endedAt };
            inactivity = now >= endedAt ? now - endedAt : TimeSpan.Zero;
        }

        // Optional AI check: once it has answered for this turn it decides whether a finished turn asked a
        // question; while its answer is awaited the turn is shown quietly, and the rules are the fallback.
        var asksUser = StatusBar.Core.Judgment.TurnVerdictPolicy.AsksUser(
            state.EndedWithQuestion, state.FinalMessageAt, state.Verdict, now) == true;
        return state.Turn switch
        {
            // ⚠️ A-X4 The answer to a question card is expected to start a new turn, which clears this.
            CodexTurnStatus.Completed when state.EndedWithStructuredQuestion && inactivity < timings.QuestionAttentionExpiry => task with
            {
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = StateConfidence.Inferred,
                AttentionReason = "Waiting for your answer",
                EvidenceKey = "question:" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            },
            CodexTurnStatus.Completed when asksUser && inactivity < timings.QuestionAttentionExpiry => task with
            {
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = StateConfidence.Inferred,
                AttentionReason = StatusBar.Core.Judgment.TurnVerdictPolicy.AttentionReason(state.FinalMessageAt, state.Verdict),
                EvidenceKey = "question:" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            },
            CodexTurnStatus.Completed => task with
            {
                Status = AgentTaskStatus.Complete,
                Confidence = StateConfidence.Confirmed,
                StatusDetail = StatusBar.Core.Judgment.TurnVerdictPolicy.Detail(state.FinalMessageAt, state.Verdict),
            },
            CodexTurnStatus.Failed => task with
            {
                Status = AgentTaskStatus.Failed,
                Confidence = StateConfidence.Confirmed,
            },
            CodexTurnStatus.Aborted => task with
            {
                Status = AgentTaskStatus.Complete,
                Confidence = StateConfidence.Confirmed,
                StatusDetail = "Stopped",
            },
            _ => task,
        };
    }

    static bool IsSubAgent(string? source) =>
        string.Equals(source, "sub-agent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "sub_agent", StringComparison.OrdinalIgnoreCase);
}
