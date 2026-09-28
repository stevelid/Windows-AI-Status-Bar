using System.Globalization;
using StatusBar.Core.Common;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Claude;

/// <summary>Maps accumulated Claude Code transcript state to the provider-neutral task model.</summary>
internal static class ClaudeCodeTaskMapper
{
    internal static AgentTask Map(ClaudeCodeSessionState state, DateTimeOffset now, TaskTimings timings)
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

        var inactivity = now >= task.LastActivity ? now - task.LastActivity : TimeSpan.Zero;
        if (state.Turn == ClaudeCodeTurnStatus.Running)
        {
            if (inactivity >= timings.ClaudeCodeWorkingUnknownAfter)
                return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale };
            if (inactivity >= timings.ClaudeCodeWorkingStaleAfter)
                return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Stale };
            return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed };
        }

        return state.Turn switch
        {
            ClaudeCodeTurnStatus.Completed when state.EndedWithQuestion && inactivity < timings.QuestionAttentionExpiry => task with
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
