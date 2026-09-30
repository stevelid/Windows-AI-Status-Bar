using System.Text;
using System.Text.Json;

namespace StatusBar.Core.Tasks;

/// <summary>How a finished turn ended.</summary>
public enum HistoryOutcome
{
    Complete,
    Failed,
    Stopped,
}

/// <summary>One finished task in the history: the latest finish seen for that task.</summary>
public sealed record HistoryEntry(
    string TaskId,
    AgentProvider Provider,
    string Title,
    HistoryOutcome Outcome,
    DateTimeOffset FinishedAt,
    string? SessionReference)
{
    /// <summary>A task the navigation code can open, built from this entry.</summary>
    public AgentTask ToTask() => new()
    {
        Id = TaskId,
        Provider = Provider,
        Title = Title,
        Status = Outcome == HistoryOutcome.Failed ? AgentTaskStatus.Failed : AgentTaskStatus.Complete,
        Confidence = StateConfidence.Confirmed,
        LastActivity = FinishedAt,
        StatusDetail = Outcome == HistoryOutcome.Stopped ? "Stopped" : null,
        SessionReference = SessionReference,
    };
}

/// <summary>
/// Keeps the latest finish of every task seen, for a limited time. In memory by default; when
/// persistence is switched on (an explicit user setting) the entries, including their short titles
/// and session references, are also written to an app-owned file so history survives a restart.
/// </summary>
public sealed class TaskHistory
{
    /// <summary>How long an entry is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>The most entries kept; the oldest go first.</summary>
    public const int MaximumEntries = 100;

    const long MaximumFileBytes = 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly object _gate = new();
    readonly Dictionary<string, HistoryEntry> _entries = new(StringComparer.Ordinal);
    readonly TimeProvider _time;
    readonly string _filePath;
    bool _persist;

    /// <summary>Raised after the entries change. May run on any thread.</summary>
    public event Action? Changed;

    /// <summary>Creates a history; <paramref name="filePath"/> is only used while persistence is on.</summary>
    public TaskHistory(TimeProvider time, string filePath, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _time = time;
        _filePath = Path.GetFullPath(filePath);
        _persist = persist;
        if (persist) Load();
    }

    /// <summary>The entries, newest first, with expired ones removed.</summary>
    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            bool pruned;
            HistoryEntry[] entries;
            lock (_gate)
            {
                pruned = Prune(_time.GetUtcNow());
                entries = Ordered();
                if (pruned) Save();
            }
            if (pruned) RaiseChanged();
            return entries;
        }
    }

    /// <summary>Whether entries are also stored in the history file.</summary>
    public bool IsPersistent
    {
        get { lock (_gate) return _persist; }
    }

    /// <summary>Turns file storage on (writing what is in memory) or off (deleting the file).</summary>
    public void SetPersistence(bool persist)
    {
        lock (_gate)
        {
            if (_persist == persist) return;
            _persist = persist;
            if (persist)
            {
                Load();
                Save();
            }
            else
            {
                DeleteFile();
            }
        }
    }

    /// <summary>Records every finished task in the list; newer finishes replace older ones per task.</summary>
    public void Observe(IEnumerable<AgentTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var changed = false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var task in tasks)
            {
                if (task.Status is not (AgentTaskStatus.Complete or AgentTaskStatus.Failed)) continue;
                if (now - task.LastActivity >= Retention) continue;
                var entry = new HistoryEntry(
                    task.Id,
                    task.Provider,
                    task.Title,
                    task.Status == AgentTaskStatus.Failed ? HistoryOutcome.Failed
                        : string.Equals(task.StatusDetail, "Stopped", StringComparison.Ordinal) ? HistoryOutcome.Stopped
                        : HistoryOutcome.Complete,
                    task.LastActivity,
                    task.SessionReference);
                if (_entries.TryGetValue(task.Id, out var existing) && existing.FinishedAt >= entry.FinishedAt)
                {
                    // Same finish seen again; keep the reference fresh if it was missing.
                    if (existing.SessionReference is null && entry.SessionReference is not null &&
                        existing.FinishedAt == entry.FinishedAt)
                    {
                        _entries[task.Id] = existing with { SessionReference = entry.SessionReference };
                        changed = true;
                    }
                    continue;
                }
                _entries[task.Id] = entry;
                changed = true;
            }

            if (Prune(now)) changed = true;
            if (changed) Save();
        }

        if (changed) RaiseChanged();
    }

    /// <summary>Removes one task's entry.</summary>
    public void Remove(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(taskId);
            if (removed) Save();
        }
        if (removed) RaiseChanged();
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        bool had;
        lock (_gate)
        {
            had = _entries.Count > 0;
            _entries.Clear();
            if (had) Save();
        }
        if (had) RaiseChanged();
    }

    HistoryEntry[] Ordered() => _entries.Values
        .OrderByDescending(entry => entry.FinishedAt)
        .ThenBy(entry => entry.TaskId, StringComparer.Ordinal)
        .ToArray();

    bool Prune(DateTimeOffset now)
    {
        var removed = false;
        foreach (var id in _entries.Values.Where(entry => now - entry.FinishedAt >= Retention).Select(entry => entry.TaskId).ToArray())
            removed |= _entries.Remove(id);
        if (_entries.Count > MaximumEntries)
        {
            foreach (var entry in Ordered().Skip(MaximumEntries))
                removed |= _entries.Remove(entry.TaskId);
        }
        return removed;
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_filePath) || new FileInfo(_filePath).Length > MaximumFileBytes) return;
            var entries = JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(_filePath, Encoding.UTF8));
            if (entries is null) return;
            var now = _time.GetUtcNow();
            foreach (var stored in entries)
            {
                if (string.IsNullOrWhiteSpace(stored.TaskId) || string.IsNullOrWhiteSpace(stored.Title)) continue;
                if (!Enum.IsDefined(stored.Provider) || !Enum.IsDefined(stored.Outcome)) continue;
                if (now - stored.FinishedAt >= Retention) continue;
                _entries[stored.TaskId] = new HistoryEntry(
                    stored.TaskId, stored.Provider, stored.Title, stored.Outcome, stored.FinishedAt, stored.SessionReference);
            }
            Prune(now);
        }
        catch (Exception)
        {
            // A damaged history file is ignored and replaced on the next save.
        }
    }

    void Save()
    {
        if (!_persist) return;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var stored = Ordered().Select(entry => new StoredEntry
            {
                TaskId = entry.TaskId,
                Provider = entry.Provider,
                Title = entry.Title,
                Outcome = entry.Outcome,
                FinishedAt = entry.FinishedAt,
                SessionReference = entry.SessionReference,
            });
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // History is a convenience; a write failure must not affect task monitoring.
        }
    }

    void DeleteFile()
    {
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

    void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try { ((Action)subscriber)(); }
            catch (Exception) { }
        }
    }

    sealed class StoredEntry
    {
        public string TaskId { get; set; } = "";
        public AgentProvider Provider { get; set; }
        public string Title { get; set; } = "";
        public HistoryOutcome Outcome { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string? SessionReference { get; set; }
    }
}
