namespace StatusBar.Core.Usage;

/// <summary>Visual severity for the tightest remaining allowance.</summary>
public enum AllowanceLevel
{
    Normal,
    Approaching,
    Low,
}

/// <summary>Calculates compact summaries from a provider's quota windows.</summary>
public static class UsageSummary
{
    /// <summary>Returns the window with the least remaining allowance, or null when there are no windows.</summary>
    /// <remarks>Ties use the earlier reset time; a window without a reset time sorts after known reset times.</remarks>
    public static UsageWindow? Principal(IReadOnlyList<UsageWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return windows
            .OrderBy(window => window.RemainingPercent)
            .ThenBy(window => window.ResetsAt ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the window shown in the compact strip: the shortest window with a known length
    /// (the five-hour session for both Codex and Claude), falling back to <see cref="Principal"/>
    /// when no window reports a length. Steve prefers the session figure on the strip; every
    /// window, including weekly limits, is still listed in the details pane.
    /// </summary>
    public static UsageWindow? Compact(IReadOnlyList<UsageWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        return windows
            .Where(window => window.Length is not null)
            .OrderBy(window => window.Length)
            .FirstOrDefault()
            ?? Principal(windows);
    }

    /// <summary>
    /// Classifies remaining allowance as Normal (at or above the approaching threshold),
    /// Approaching (at or above the low threshold), or Low (below the low threshold).
    /// </summary>
    public static AllowanceLevel Level(
        double remainingPercent,
        double approachingBelow = 30,
        double lowBelow = 10)
    {
        if (remainingPercent < lowBelow) return AllowanceLevel.Low;
        if (remainingPercent < approachingBelow) return AllowanceLevel.Approaching;
        return AllowanceLevel.Normal;
    }
}
