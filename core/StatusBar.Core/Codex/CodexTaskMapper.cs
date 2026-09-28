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
            CodexTurnStatus.Completed when state.EndedWithQuestion && inactivity < timings.QuestionAttentionExpiry => task with
            {
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = StateConfidence.Inferred,
                AttentionReason = "Asked you a question",
                EvidenceKey = "question:" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            },
            CodexTurnStatus.Completed => task with
            {
                Status = AgentTaskStatus.Complete,
                Confidence = StateConfidence.Confirmed,
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
