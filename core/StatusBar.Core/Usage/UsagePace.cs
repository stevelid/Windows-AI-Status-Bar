namespace StatusBar.Core.Usage;

/// <summary>How a window's remaining allowance compares with the time left in it.</summary>
/// <param name="TimeRemainingPercent">Share of the window's length still to run, 0–100.</param>
/// <param name="Level">Severity of running ahead of the calendar; Normal when on pace or better.</param>
public readonly record struct UsagePace(double TimeRemainingPercent, AllowanceLevel Level);

/// <summary>Compares quota burn with elapsed time so the strip can colour by pace, not just level.</summary>
public static class UsagePaceCalculator
{
    /// <summary>Allowance may trail the calendar by this many points before the pace turns amber.</summary>
    public const double ApproachingMargin = 10;

    /// <summary>Allowance may trail the calendar by this many points before the pace turns red.</summary>
    public const double LowMargin = 25;

    /// <summary>
    /// Returns the pace for a window, or null when its length or reset time is unknown.
    /// A window that is nearly over is never behind: leftover allowance is about to expire.
    /// </summary>
    public static UsagePace? Evaluate(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Length is not TimeSpan length || length <= TimeSpan.Zero || window.ResetsAt is not DateTimeOffset resetsAt)
            return null;

        var timeRemaining = Math.Clamp((resetsAt - now) / length * 100, 0, 100);
        var shortfall = timeRemaining - window.RemainingPercent;
        var level = shortfall >= LowMargin ? AllowanceLevel.Low
            : shortfall >= ApproachingMargin ? AllowanceLevel.Approaching
            : AllowanceLevel.Normal;
        return new UsagePace(timeRemaining, level);
    }

    /// <summary>The more severe of the absolute level (few percent left) and the pace level.</summary>
    public static AllowanceLevel Worst(
        UsageWindow window,
        DateTimeOffset now,
        double approachingBelow = 30,
        double lowBelow = 10)
    {
        var absolute = UsageSummary.Level(window.RemainingPercent, approachingBelow, lowBelow);
        var pace = Evaluate(window, now)?.Level ?? AllowanceLevel.Normal;
        return (AllowanceLevel)Math.Max((int)absolute, (int)pace);
    }
}
