namespace StatusBar.Core.Claude;

internal enum ClaudeCodeTurnStatus
{
    None,
    Running,
    Completed,
    Aborted,
}

internal sealed record ClaudePendingTool(string ToolUseId, DateTimeOffset Since);

/// <summary>In-memory state accumulated from one Claude Code transcript.</summary>
internal sealed class ClaudeCodeSessionState
{
    internal string? SessionId { get; set; }
    internal string? CwdLeaf { get; set; }
    internal string? CustomTitleCandidate { get; set; }
    internal string? AiTitleCandidate { get; set; }
    internal string? FirstPromptTitleCandidate { get; set; }
    internal DateTimeOffset LastActivity { get; set; }
    internal bool HasActivity { get; set; }
    internal bool HasSeenTurnEvent { get; set; }
    internal bool HasSidechainActivity { get; set; }
    internal bool HasNonSidechainActivity { get; set; }
    internal bool IsSidechainOnly => HasSidechainActivity && !HasNonSidechainActivity;
    internal bool EndedWithQuestion { get; set; }
    internal ClaudeCodeTurnStatus Turn { get; set; }
    internal Dictionary<string, ClaudePendingTool> PendingTools { get; } = new(StringComparer.Ordinal);
}
