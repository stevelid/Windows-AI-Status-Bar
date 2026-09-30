namespace StatusBar.Core.Tasks;

/// <summary>A stretch of time during which one conversation was working.</summary>
/// <param name="TaskId">The task's id, e.g. <c>codex:1a2b…</c>.</param>
/// <param name="Provider">The provider whose allowance the work used.</param>
/// <param name="Title">The task's short title. Held in memory only.</param>
/// <param name="Start">When it was first seen working.</param>
/// <param name="End">When it stopped working; null while it still is.</param>
public sealed record ActivityInterval(string TaskId, AgentProvider Provider, string Title, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>
/// Remembers when each conversation was working over the last few hours, so the usage chart can say what
/// was running at a given time and how much allowance each conversation probably used. In memory only:
/// titles are never written to disk here. Times are when the app saw a task start and stop working.
/// </summary>
public sealed class TaskActivityLog
{
    /// <summary>How long finished intervals are kept (matches the usage readings).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(3);

    /// <summary>The most intervals kept; the oldest go first.</summary>
    public const int MaximumIntervals = 300;

    readonly object _gate = new();
    readonly TimeProvider _time;
    readonly List<ActivityInterval> _finished = new();
    readonly Dictionary<string, ActivityInterval> _open = new(StringComparer.Ordinal);

    /// <summary>Creates an empty log.</summary>
    public TaskActivityLog(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// Updates the log from the providers' current task lists: a task that is working (and not merely stale)
    /// opens an interval, and one that stops or disappears closes it.
    /// </summary>
    public void Observe(IEnumerable<AgentTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var running = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                // A "working" task with no recent activity is probably not using the allowance any more.
                if (task.Status != AgentTaskStatus.Working || task.Confidence == StateConfidence.Stale) continue;
                running.Add(task.Id);
                if (_open.TryGetValue(task.Id, out var open))
                {
                    if (open.Title != task.Title) _open[task.Id] = open with { Title = task.Title };
                }
                else
                {
                    _open[task.Id] = new ActivityInterval(task.Id, task.Provider, task.Title, now, null);
                }
            }

            foreach (var id in _open.Keys.Where(id => !running.Contains(id)).ToArray())
            {
                _finished.Add(_open[id] with { End = now });
                _open.Remove(id);
            }

            Prune(now);
        }
    }

    /// <summary>All intervals for a provider (finished and still open), oldest first.</summary>
    public IReadOnlyList<ActivityInterval> Intervals(AgentProvider provider)
    {
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            return _finished.Concat(_open.Values)
                .Where(interval => interval.Provider == provider)
                .OrderBy(interval => interval.Start)
                .ToArray();
        }
    }

    /// <summary>The distinct conversations of a provider that were working at any time in [from, to].</summary>
    public IReadOnlyList<ActivityInterval> RunningBetween(AgentProvider provider, DateTimeOffset from, DateTimeOffset to)
    {
        var now = _time.GetUtcNow();
        return Intervals(provider)
            .Where(interval => interval.Start <= to && (interval.End ?? now) >= from)
            .GroupBy(interval => interval.TaskId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(interval => interval.Start).First())
            .ToArray();
    }

    void Prune(DateTimeOffset now)
    {
        _finished.RemoveAll(interval => interval.End is { } end && now - end >= Retention);
        if (_finished.Count > MaximumIntervals) _finished.RemoveRange(0, _finished.Count - MaximumIntervals);
    }
}
