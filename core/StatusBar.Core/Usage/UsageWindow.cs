namespace StatusBar.Core.Usage;

/// <summary>Identifies the provider that returned a usage snapshot.</summary>
public enum UsageSource
{
    Codex,
    Claude,
}

/// <summary>One provider quota window, such as a five-hour session or a weekly allowance.</summary>
/// <remarks>
/// <c>UsedPercent</c> is expected to be on a 0–100 scale. <c>Length</c> is the window's duration
/// when the provider adapter knows it (e.g. 5 hours for a session, 7 days for a weekly limit);
/// it lets the strip pick the session window without knowing provider key names.
/// </remarks>
public sealed record UsageWindow(
    string Key,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Length = null)
{
    /// <summary>Percentage left in this window, clamped to the 0–100 range.</summary>
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
