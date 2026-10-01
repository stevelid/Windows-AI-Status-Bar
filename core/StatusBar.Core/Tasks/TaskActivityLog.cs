using System.Text;
using System.Text.Json;

namespace StatusBar.Core.Tasks;

/// <summary>A stretch of time during which one conversation was working.</summary>
/// <param name="TaskId">The task's id, e.g. <c>codex:1a2b…</c>.</param>
/// <param name="Provider">The provider whose allowance the work used.</param>
/// <param name="Title">The task's short title. Held in memory only; empty for an interval loaded from disk.</param>
/// <param name="Start">When it was first seen working.</param>
/// <param name="End">When it stopped working; null while it still is.</param>
public sealed record ActivityInterval(string TaskId, AgentProvider Provider, string Title, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>
/// Remembers when each conversation was working over the last few hours, so the usage chart can say what
/// was running at a given time and how much allowance each conversation probably used. Titles are never
/// written to disk: while persistence is on, only task id, provider and start/end times are saved (in an
/// app-owned file, with the same switch as the usage readings), so a restart does not forget who was
/// running. Intervals loaded from disk therefore have an empty title.
/// </summary>
/// <remarks>
/// Also remembers when the app was watching (this run, plus earlier runs from the file), because "nothing
/// was working" and "the app was not running" mean different things for a stretch of the usage chart.
/// </remarks>
public sealed class TaskActivityLog
{
    /// <summary>How long finished intervals are kept (matches the usage readings).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(3);

    /// <summary>The most intervals kept; the oldest go first.</summary>
    public const int MaximumIntervals = 300;

    const int MaximumWatchedSpans = 50;
    const long MaximumFileBytes = 1024 * 1024;
    const int MaximumIdLength = 200;
    static readonly TimeSpan MinimumWriteInterval = TimeSpan.FromSeconds(30);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    readonly object _gate = new();
    readonly TimeProvider _time;
    readonly string? _filePath;
    readonly List<ActivityInterval> _finished = new();
    readonly Dictionary<string, ActivityInterval> _open = new(StringComparer.Ordinal);
    // When an open interval's task was last seen still working: this is where a crash or exit closes it.
    readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    // Stretches earlier runs of the app were watching, loaded from the file.
    readonly List<(DateTimeOffset Start, DateTimeOffset End)> _earlierRuns = new();
    readonly DateTimeOffset _runStart;
    bool _persist;
    DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    /// <summary>Creates a log; <paramref name="filePath"/> is only used while persistence is on.</summary>
    public TaskActivityLog(TimeProvider time, string? filePath = null, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _runStart = time.GetUtcNow();
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _persist = persist && _filePath is not null;
        if (_persist) Load();
    }

    /// <summary>Whether activity is also stored in the history file.</summary>
    public bool IsPersistent
    {
        get { lock (_gate) return _persist; }
    }

    /// <summary>Turns file storage on (loading and writing) or off (deleting the file).</summary>
    public void SetPersistence(bool persist)
    {
        lock (_gate)
        {
            persist = persist && _filePath is not null;
            if (_persist == persist) return;
            _persist = persist;
            if (persist)
            {
                Load();
                Save(force: true);
            }
            else
            {
                DeleteFile();
            }
        }
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
                _lastSeen[task.Id] = now;
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
                _lastSeen.Remove(id);
            }

            Prune(now);
            Save(force: false);
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

    /// <summary>
    /// Whether the app was watching for tasks at any time in [from, to], this run or an earlier one still on
    /// file. False means a quiet-looking stretch is really unknown, because the app was not running.
    /// </summary>
    public bool WasWatching(DateTimeOffset from, DateTimeOffset to)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_runStart <= to && now >= from) return true;
            return _earlierRuns.Any(span => span.Start <= to && span.End >= from);
        }
    }

    void Prune(DateTimeOffset now)
    {
        _finished.RemoveAll(interval => interval.End is { } end && now - end >= Retention);
        if (_finished.Count > MaximumIntervals) _finished.RemoveRange(0, _finished.Count - MaximumIntervals);
        _earlierRuns.RemoveAll(span => now - span.End >= Retention);
    }

    void Load()
    {
        try
        {
            if (_filePath is null || !File.Exists(_filePath) || new FileInfo(_filePath).Length > MaximumFileBytes) return;
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_filePath, Encoding.UTF8));
            if (stored is null) return;
            var now = _time.GetUtcNow();
            var limit = now + TimeSpan.FromMinutes(5);

            // An interval that was still open when the file was written is stored with its last-seen time as
            // its end, so it comes back closed there rather than running on for the time the app was off.
            var known = new HashSet<(string, DateTimeOffset)>(_finished.Concat(_open.Values).Select(interval => (interval.TaskId, interval.Start)));
            foreach (var item in stored.Intervals ?? new List<StoredInterval>())
            {
                if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > MaximumIdLength || !Enum.IsDefined(item.Provider)) continue;
                var start = DateTimeOffset.FromUnixTimeSeconds(item.Start);
                var end = DateTimeOffset.FromUnixTimeSeconds(item.End);
                if (end < start || end > limit || now - end >= Retention) continue;
                if (!known.Add((item.Id, start))) continue;
                _finished.Add(new ActivityInterval(item.Id, item.Provider, "", start, end));
            }

            _finished.Sort((a, b) => a.Start.CompareTo(b.Start));

            foreach (var pair in stored.Watched ?? new List<long[]>())
            {
                if (pair.Length != 2) continue;
                var start = DateTimeOffset.FromUnixTimeSeconds(pair[0]);
                var end = DateTimeOffset.FromUnixTimeSeconds(pair[1]);
                if (end < start || end > limit || now - end >= Retention) continue;
                _earlierRuns.Add((start, end));
            }

            Prune(now);
        }
        catch (Exception)
        {
            // A damaged activity file is ignored and replaced on the next save.
        }
    }

    void Save(bool force)
    {
        if (!_persist || _filePath is null) return;
        var now = _time.GetUtcNow();
        if (!force && now - _lastWrite < MinimumWriteInterval) return;
        _lastWrite = now;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var intervals = _finished
                .Where(interval => interval.End is not null)
                .Select(interval => new StoredInterval(interval.TaskId, interval.Provider, interval.Start.ToUnixTimeSeconds(), interval.End!.Value.ToUnixTimeSeconds()))
                .Concat(_open.Values.Select(interval => new StoredInterval(
                    interval.TaskId,
                    interval.Provider,
                    interval.Start.ToUnixTimeSeconds(),
                    _lastSeen.GetValueOrDefault(interval.TaskId, now).ToUnixTimeSeconds())))
                .ToList();

            var watched = _earlierRuns
                .Select(span => new[] { span.Start.ToUnixTimeSeconds(), span.End.ToUnixTimeSeconds() })
                .TakeLast(MaximumWatchedSpans - 1)
                .Append(new[] { _runStart.ToUnixTimeSeconds(), now.ToUnixTimeSeconds() })
                .ToList();

            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StoredFile { Intervals = intervals, Watched = watched }, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // The record is a convenience; a write failure must not affect task monitoring.
        }
    }

    void DeleteFile()
    {
        if (_filePath is null) return;
        try
        {
            File.Delete(_filePath);
            File.Delete(_filePath + ".tmp");
        }
        catch (Exception)
        {
            // Nothing to do if the file is locked or already gone.
        }
    }

    // Short property names keep the file small; there is deliberately no title field.
    sealed class StoredFile
    {
        public List<StoredInterval>? Intervals { get; set; }
        public List<long[]>? Watched { get; set; }
    }

    sealed record StoredInterval(string Id, AgentProvider Provider, long Start, long End);
}
