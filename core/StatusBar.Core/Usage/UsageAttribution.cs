using StatusBar.Core.Tasks;

namespace StatusBar.Core.Usage;

/// <summary>An estimate of how much of a window's allowance each conversation used.</summary>
/// <param name="PointsByTask">Percentage points of the window attributed to each task id.</param>
/// <param name="UnattributedPoints">Points used while none of the app's conversations were working (another device, the web apps, or an untracked session).</param>
/// <param name="TotalPoints">All points used in the period.</param>
public sealed record UsageAttribution(
    IReadOnlyDictionary<string, double> PointsByTask,
    double UnattributedPoints,
    double TotalPoints);

/// <summary>
/// Shares each drop in a window's remaining allowance among the conversations that were working while it
/// fell, in proportion to how long each was working in that stretch. This is an estimate, not a meter: the
/// allowance falls for the whole account, so use from other devices cannot be told apart, and overlapping
/// conversations can only be split by time.
/// </summary>
public static class UsageAttributionCalculator
{
    /// <summary>
    /// Attributes the drops in <paramref name="readings"/> (oldest first) that happened within <paramref name="lookBack"/>
    /// of <paramref name="now"/> to the given conversations' working intervals.
    /// </summary>
    public static UsageAttribution Compute(
        IReadOnlyList<(DateTimeOffset At, double Remaining)> readings,
        IReadOnlyList<ActivityInterval> intervals,
        DateTimeOffset now,
        TimeSpan lookBack)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(intervals);
        var byTask = new Dictionary<string, double>(StringComparer.Ordinal);
        double unattributed = 0, total = 0;
        var since = now - lookBack;

        for (var i = 1; i < readings.Count; i++)
        {
            var before = readings[i - 1];
            var after = readings[i];
            if (after.At <= since) continue;
            if (after.At - before.At > UsageRateHistory.GapAfter) continue;

            var drop = before.Remaining - after.Remaining;
            // A rise is a window reset and costs nothing; no change is no use.
            if (drop <= 0) continue;
            total += drop;

            // Time each conversation spent working within this stretch between two readings.
            var overlaps = new List<(string TaskId, double Seconds)>();
            foreach (var group in intervals.GroupBy(interval => interval.TaskId, StringComparer.Ordinal))
            {
                var seconds = 0.0;
                foreach (var interval in group)
                {
                    var start = interval.Start > before.At ? interval.Start : before.At;
                    var end = (interval.End ?? now) < after.At ? (interval.End ?? now) : after.At;
                    if (end > start) seconds += (end - start).TotalSeconds;
                }

                if (seconds > 0) overlaps.Add((group.Key, seconds));
            }

            var sum = overlaps.Sum(overlap => overlap.Seconds);
            if (sum <= 0)
            {
                unattributed += drop;
                continue;
            }

            foreach (var (taskId, seconds) in overlaps)
                byTask[taskId] = byTask.GetValueOrDefault(taskId) + drop * seconds / sum;
        }

        return new UsageAttribution(byTask, unattributed, total);
    }
}
