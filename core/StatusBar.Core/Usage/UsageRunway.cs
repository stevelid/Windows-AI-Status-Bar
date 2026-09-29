namespace StatusBar.Core.Usage;

/// <summary>
/// Whether a quota window is on course to run out before it resets, judged from the recent
/// rate of use rather than from the calendar.
/// </summary>
/// <param name="Level">Normal: lasts until reset (or not enough data). Approaching: runs out before
/// reset at the recent rate. Low: runs out within <see cref="UsageBurnTracker.SoonThreshold"/>, or
/// already below the low threshold.</param>
/// <param name="TimeToEmpty">Projected time until the window is used up at the recent rate; null when
/// the rate is unknown or zero.</param>
/// <param name="TimeToReset">Time until the window resets; null when the reset time is unknown.</param>
public readonly record struct UsageRunway(AllowanceLevel Level, TimeSpan? TimeToEmpty, TimeSpan? TimeToReset)
{
    /// <summary>A window with no concern and no projection.</summary>
    public static UsageRunway Unknown { get; } = new(AllowanceLevel.Normal, null, null);

    /// <summary>True when the window is projected to run out before it resets.</summary>
    public bool RunsOutBeforeReset =>
        TimeToEmpty is TimeSpan empty && TimeToReset is TimeSpan reset && empty < reset;
}

/// <summary>
/// Keeps a short history of usage readings per quota window and estimates how fast each is being
/// used, so the strip can warn only when the allowance will not last until it resets.
/// </summary>
/// <remarks>
/// Readings come from successful refreshes (about once a minute). A session window uses the last
/// 30 minutes of readings and a longer window the last 6 hours, so one busy minute does not raise a
/// warning. A window's history restarts when it resets (allowance rises, or the reset time moves on).
/// Thread-safe: the usage monitor records from worker threads while the UI reads.
/// </remarks>
public sealed class UsageBurnTracker
{
    /// <summary>Windows up to this length use the short (session) look-back.</summary>
    public static readonly TimeSpan SessionMaxLength = TimeSpan.FromHours(6);

    /// <summary>Projected to run out within this time counts as Low.</summary>
    public static readonly TimeSpan SoonThreshold = TimeSpan.FromMinutes(30);

    static readonly TimeSpan SessionLookBack = TimeSpan.FromMinutes(30);
    static readonly TimeSpan SessionMinimumSpan = TimeSpan.FromMinutes(10);
    static readonly TimeSpan LongLookBack = TimeSpan.FromHours(6);
    static readonly TimeSpan LongMinimumSpan = TimeSpan.FromHours(1);

    // Readings are whole or near-whole percentages; a rise larger than this means the window reset.
    const double ResetRiseThreshold = 1.0;

    // Below this burn rate the window is treated as idle rather than projected to run out.
    const double MinimumRatePercentPerHour = 0.1;

    const int MinimumSamples = 3;

    readonly object _gate = new();
    readonly Dictionary<(UsageSource Source, string Key), WindowHistory> _histories = new();

    /// <summary>Adds the readings from one successful refresh.</summary>
    public void Record(UsageSource source, IReadOnlyList<UsageWindow> windows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(windows);
        lock (_gate)
        {
            foreach (var window in windows)
            {
                if (window is null || string.IsNullOrEmpty(window.Key)) continue;
                var key = (source, window.Key);
                if (!_histories.TryGetValue(key, out var history))
                {
                    history = new WindowHistory();
                    _histories[key] = history;
                }

                history.Add(window, now);
            }
        }
    }

    /// <summary>Projects whether a window lasts until its reset at the recent rate of use.</summary>
    /// <param name="lowBelowPercent">Remaining percentage below which the window is Low regardless of rate.</param>
    public UsageRunway Assess(UsageSource source, UsageWindow window, DateTimeOffset now, double lowBelowPercent = 10)
    {
        ArgumentNullException.ThrowIfNull(window);
        var remaining = window.RemainingPercent;
        TimeSpan? timeToReset = window.ResetsAt is DateTimeOffset resetsAt
            ? (resetsAt > now ? resetsAt - now : TimeSpan.Zero)
            : null;

        double? rate;
        lock (_gate)
        {
            rate = _histories.TryGetValue((source, window.Key), out var history)
                ? history.RatePercentPerHour(window, now)
                : null;
        }

        TimeSpan? timeToEmpty = rate is double perHour && perHour >= MinimumRatePercentPerHour
            ? TimeSpan.FromHours(remaining / perHour)
            : null;

        var level = AllowanceLevel.Normal;
        if (timeToEmpty is TimeSpan empty && timeToReset is TimeSpan reset && empty < reset)
            level = empty <= SoonThreshold ? AllowanceLevel.Low : AllowanceLevel.Approaching;
        if (remaining < lowBelowPercent)
            level = AllowanceLevel.Low;

        return new UsageRunway(level, timeToEmpty, timeToReset);
    }

    /// <summary>The most severe runway among a provider's windows, with the window it belongs to.</summary>
    public (UsageWindow? Window, UsageRunway Runway) Worst(
        UsageSource source,
        IReadOnlyList<UsageWindow> windows,
        DateTimeOffset now,
        double lowBelowPercent = 10)
    {
        ArgumentNullException.ThrowIfNull(windows);
        UsageWindow? worstWindow = null;
        var worst = UsageRunway.Unknown;
        foreach (var window in windows)
        {
            if (window is null) continue;
            var runway = Assess(source, window, now, lowBelowPercent);
            // Prefer the more severe level; for equal levels, the one that empties soonest.
            if (worstWindow is null || runway.Level > worst.Level ||
                (runway.Level == worst.Level && runway.Level != AllowanceLevel.Normal &&
                 (runway.TimeToEmpty ?? TimeSpan.MaxValue) < (worst.TimeToEmpty ?? TimeSpan.MaxValue)))
            {
                worstWindow = window;
                worst = runway;
            }
        }

        return (worstWindow, worst);
    }

    sealed class WindowHistory
    {
        readonly List<(DateTimeOffset At, double Remaining)> _samples = new();
        DateTimeOffset? _resetsAt;

        public void Add(UsageWindow window, DateTimeOffset now)
        {
            var remaining = window.RemainingPercent;
            if (_samples.Count > 0 && (remaining > _samples[^1].Remaining + ResetRiseThreshold || ResetMovedOn(window)))
                _samples.Clear();

            _resetsAt = window.ResetsAt;
            if (_samples.Count > 0 && _samples[^1].At >= now) return;
            _samples.Add((now, remaining));

            var keepFrom = now - LongLookBack;
            _samples.RemoveAll(sample => sample.At < keepFrom);
        }

        public double? RatePercentPerHour(UsageWindow window, DateTimeOffset now)
        {
            var isSession = window.Length is not TimeSpan length || length <= SessionMaxLength;
            var lookBack = isSession ? SessionLookBack : LongLookBack;
            var minimumSpan = isSession ? SessionMinimumSpan : LongMinimumSpan;

            var recent = _samples.Where(sample => sample.At >= now - lookBack).ToArray();
            if (recent.Length < MinimumSamples) return null;
            var span = recent[^1].At - recent[0].At;
            if (span < minimumSpan) return null;

            var used = recent[0].Remaining - recent[^1].Remaining;
            return Math.Max(0, used) / span.TotalHours;
        }

        // A later reset time by more than a tenth of the window (or 30 minutes when the length is
        // unknown) means a new window began; small shifts are provider rounding.
        bool ResetMovedOn(UsageWindow window)
        {
            if (_resetsAt is not DateTimeOffset previous || window.ResetsAt is not DateTimeOffset current) return false;
            var tolerance = window.Length is TimeSpan length && length > TimeSpan.Zero
                ? length / 10
                : TimeSpan.FromMinutes(30);
            return current - previous > tolerance;
        }
    }
}
