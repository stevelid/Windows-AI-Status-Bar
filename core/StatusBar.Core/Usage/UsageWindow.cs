namespace StatusBar.Core.Usage;

/// <summary>Identifies the provider that returned a usage snapshot.</summary>
public enum UsageSource
{
    Codex,
    Claude,
}

/// <summary>One provider quota window, such as a five-hour session or a weekly allowance.</summary>
/// <remarks><c>UsedPercent</c> is expected to be on a 0–100 scale.</remarks>
public sealed record UsageWindow(string Key, string Label, double UsedPercent, DateTimeOffset? ResetsAt)
{
    /// <summary>Percentage left in this window, clamped to the 0–100 range.</summary>
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
