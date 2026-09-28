namespace StatusBar.Core.Tasks;

/// <summary>The desktop application that owns a task.</summary>
public enum AgentProvider
{
    Codex,
    Claude,
}

/// <summary>Normalized task state. Provider adapters map their own evidence onto these values.</summary>
public enum AgentTaskStatus
{
    Working,
    NeedsAttention,
    Complete,
    Failed,
    Unknown,
}

/// <summary>
/// How strongly the evidence supports <see cref="AgentTaskStatus"/>.
/// Confirmed = an explicit record said so; Inferred = derived from timing or indirect signals;
/// Stale = the last evidence is old enough that the status may no longer be true.
/// </summary>
public enum StateConfidence
{
    Confirmed,
    Inferred,
    Stale,
}

/// <summary>
/// One AI task as the UI sees it. The UI must only consume this type; it must never read
/// provider files, JSON property names or log strings directly.
/// </summary>
/// <remarks>
/// Immutable so that snapshots can be compared and handed across threads safely.
/// Providers publish a new instance (using <c>with</c>) whenever something changes.
/// </remarks>
public sealed record AgentTask
{
    /// <summary>Stable, provider-prefixed identifier, e.g. "codex:&lt;thread-id&gt;".</summary>
    public required string Id { get; init; }

    public required AgentProvider Provider { get; init; }

    /// <summary>Short display title. Never a full prompt.</summary>
    public required string Title { get; init; }

    public required AgentTaskStatus Status { get; init; }

    public required StateConfidence Confidence { get; init; }

    /// <summary>Time of the most recent evidence of activity for this task.</summary>
    public required DateTimeOffset LastActivity { get; init; }

    /// <summary>Short, content-free reason such as "Approval requested" or "Waiting for input".</summary>
    public string? AttentionReason { get; init; }

    /// <summary>Short, content-free status context shown in the task tooltip.</summary>
    public string? StatusDetail { get; init; }

    /// <summary>Opaque provider reference used to focus or open the session. Never shown to the user.</summary>
    public string? SessionReference { get; init; }
}
