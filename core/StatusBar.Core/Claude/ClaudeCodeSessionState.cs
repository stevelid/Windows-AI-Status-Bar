namespace StatusBar.Core.Claude;

internal enum ClaudeCodeTurnStatus
{
    None,
    Running,
    Completed,
    Aborted,
}

/// <summary>What a pending tool call is waiting for. Only the tool's name decides this; its input is never read.</summary>
internal enum ClaudePendingToolKind
{
    /// <summary>Any ordinary tool (Read, Bash, ...): the tool is running or awaiting a permission prompt.</summary>
    Other,

    /// <summary>A structured multiple-choice question (<c>AskUserQuestion</c>).</summary>
    Question,

    /// <summary>A plan waiting for approval (<c>ExitPlanMode</c>).</summary>
    PlanApproval,
}

internal sealed record ClaudePendingTool(
    string ToolUseId,
    DateTimeOffset Since,
    ClaudePendingToolKind Kind = ClaudePendingToolKind.Other);

/// <summary>Activity of a session's subagent transcripts (<c>&lt;session&gt;/subagents/agent-*.jsonl</c>).</summary>
internal sealed record ClaudeSubagentActivity(DateTimeOffset LastActivity, bool Running);

/// <summary>In-memory state accumulated from one Claude Code transcript.</summary>
internal sealed class ClaudeCodeSessionState
{
    internal string? SessionId { get; set; }
    internal string? CwdLeaf { get; set; }
    internal string? CustomTitleCandidate { get; set; }
    internal string? AiTitleCandidate { get; set; }
    internal string? FirstPromptTitleCandidate { get; set; }
    internal DateTimeOffset LastActivity { get; set; }

    /// <summary>When the last turn completed or was stopped; finished tasks are dated by this (see Codex).</summary>
    internal DateTimeOffset? TurnEndedAt { get; set; }
    internal bool HasActivity { get; set; }
    internal bool HasSeenTurnEvent { get; set; }
    internal bool HasSidechainActivity { get; set; }
    internal bool HasNonSidechainActivity { get; set; }
    internal bool IsSidechainOnly => HasSidechainActivity && !HasNonSidechainActivity;
    internal bool EndedWithQuestion { get; set; }

    /// <summary>Tail of the last final message, only while the optional AI check is on. Never logged or persisted.</summary>
    internal string? FinalMessageTail { get; set; }

    /// <summary>When <see cref="FinalMessageTail"/> was captured; changes once per finished turn.</summary>
    internal DateTimeOffset? FinalMessageAt { get; set; }
    internal ClaudeCodeTurnStatus Turn { get; set; }
    internal Dictionary<string, ClaudePendingTool> PendingTools { get; } = new(StringComparer.Ordinal);

    /// <summary>Background agents and shells launched by this session that have not reported back (opaque ids only).</summary>
    internal Dictionary<string, DateTimeOffset> BackgroundTasks { get; } = new(StringComparer.Ordinal);

    /// <summary>For a subagent transcript: its last assistant record ended its turn.</summary>
    internal bool SidechainEnded { get; set; }
}
