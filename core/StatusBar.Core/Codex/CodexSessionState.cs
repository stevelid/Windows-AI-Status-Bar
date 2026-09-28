namespace StatusBar.Core.Codex;

internal enum CodexTurnStatus
{
    None,
    Running,
    Completed,
    Failed,
    Aborted,
}

internal enum CodexPendingKind
{
    Approval,
    Input,
}

internal sealed record CodexPendingCall(string CallId, CodexPendingKind Kind, DateTimeOffset Since);

/// <summary>Mutable state accumulated from one Codex rollout.</summary>
internal sealed class CodexSessionState
{
    internal string? ThreadId { get; set; }

    /// <summary>
    /// Id taken from the rollout file name (rollout-&lt;time&gt;-&lt;uuid&gt;.jsonl). Used when
    /// session_meta was not read, so two such sessions never share one task id.
    /// </summary>
    internal string? FileKey { get; set; }
    internal string? Source { get; set; }
    internal string? ParentThreadId { get; set; }
    internal string? CwdLeaf { get; set; }
    internal DateTimeOffset? StartedAt { get; set; }
    internal string? ApprovalPolicy { get; set; }
    internal CodexTurnStatus Turn { get; set; }
    internal DateTimeOffset LastActivity { get; set; }
    internal bool HasActivity { get; set; }
    internal bool EndedWithQuestion { get; set; }
    internal string? TurnId { get; set; }
    internal string? AbortReason { get; set; }
    internal string? TitleCandidate { get; set; }
    internal bool HasSeenTurnEvent { get; set; }
    internal Dictionary<string, CodexPendingCall> PendingCalls { get; } = new(StringComparer.Ordinal);
}
