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
    /// <summary>The session is Codex's own approval-review ("guardian") run, not a task Steve started.</summary>
    internal bool IsGuardianReview { get; set; }

    /// <summary>Tail of the last final message, only while the optional AI check is on. Never logged or persisted.</summary>
    internal string? FinalMessageTail { get; set; }

    /// <summary>Start of the last final message when it was longer than the tail; same handling as the tail.</summary>
    internal string? FinalMessageStart { get; set; }

    /// <summary>When <see cref="FinalMessageTail"/> was captured; changes once per finished turn.</summary>
    internal DateTimeOffset? FinalMessageAt { get; set; }

    /// <summary>The AI's answer for the current finished turn, once it arrives.</summary>
    internal StatusBar.Core.Judgment.TurnVerdict? Verdict { get; set; }
    internal string? CwdLeaf { get; set; }
    internal DateTimeOffset? StartedAt { get; set; }
    internal string? ApprovalPolicy { get; set; }
    internal CodexTurnStatus Turn { get; set; }
    internal DateTimeOffset LastActivity { get; set; }

    /// <summary>
    /// When the last turn completed, failed or was stopped. Finished tasks are dated by this, not by
    /// <see cref="LastActivity"/>: opening an old thread in the Codex app appends housekeeping records,
    /// which must not make it look as if it had just finished.
    /// </summary>
    internal DateTimeOffset? TurnEndedAt { get; set; }
    internal bool HasActivity { get; set; }
    internal bool EndedWithQuestion { get; set; }

    /// <summary>The running turn posted a structured question card (request_user_input_async).</summary>
    internal bool AskedStructuredQuestion { get; set; }

    /// <summary>The last turn completed with a structured question card still open for Steve.</summary>
    internal bool EndedWithStructuredQuestion { get; set; }
    internal string? TurnId { get; set; }
    internal string? AbortReason { get; set; }
    internal string? TitleCandidate { get; set; }
    internal bool HasSeenTurnEvent { get; set; }
    internal Dictionary<string, CodexPendingCall> PendingCalls { get; } = new(StringComparer.Ordinal);
}
