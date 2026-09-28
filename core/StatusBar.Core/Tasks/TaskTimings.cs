namespace StatusBar.Core.Tasks;

/// <summary>Central timing constants for provider reconciliation and task-state rules.</summary>
public sealed record TaskTimings
{
    /// <summary>Directory reconciliation interval for task providers.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Delay used to coalesce filesystem watcher bursts.</summary>
    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a pending Codex approval must remain before it is shown.</summary>
    public TimeSpan CodexApprovalDebounce { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Age after which an inactive Codex task is marked stale.</summary>
    public TimeSpan CodexWorkingStaleAfter { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Age after which an inactive Codex task becomes unknown.</summary>
    public TimeSpan CodexWorkingUnknownAfter { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Maximum age of a Codex rollout file considered for tracking.</summary>
    public TimeSpan CodexRecentFileWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Recent metadata-only activity window for Claude tasks.</summary>
    public TimeSpan ClaudeRecentActivityWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Age after which inactive Claude work is marked stale.</summary>
    public TimeSpan ClaudeWorkingStaleAfter { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Expiry for unresolved inferred Claude attention.</summary>
    public TimeSpan ClaudeInferredAttentionExpiry { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Expiry for unresolved confirmed Claude attention.</summary>
    public TimeSpan ClaudeConfirmedAttentionExpiry { get; init; } = TimeSpan.FromHours(2);

    /// <summary>How long completed or failed tasks remain visible.</summary>
    public TimeSpan RecentlyCompletedFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long unknown tasks remain visible.</summary>
    public TimeSpan UnknownVisibleFor { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Cycle duration for the scripted demo task.</summary>
    public TimeSpan DemoCycle { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Shared defaults from the assessment plan.</summary>
    public static TaskTimings Default { get; } = new();
}
