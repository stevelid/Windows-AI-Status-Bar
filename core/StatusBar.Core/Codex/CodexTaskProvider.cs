using StatusBar.Core.IO;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Watches recent Codex rollouts and publishes normalized task snapshots.</summary>
internal sealed class CodexTaskProvider : IAgentTaskProvider
{
    static readonly TimeSpan ActiveRecheckInterval = TimeSpan.FromSeconds(5);

    readonly object _gate = new();
    readonly SemaphoreSlim _reconcileGate = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly CodexPaths _paths;
    readonly TimeProvider _time;
    readonly TaskTimings _timings;
    readonly bool _watchFiles;
    readonly CodexSessionReader _sessions;
    ProviderTaskSnapshot _current;
    DirectoryWatcher? _watcher;
    ITimer? _timer;
    bool _started;
    bool _disposed;
    bool _watcherUnavailable;
    int _watcherOverflowCount;

    internal CodexTaskProvider(
        CodexPaths paths,
        TimeProvider time,
        TaskTimings? timings = null,
        bool watchFiles = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        _paths = paths;
        _time = time;
        _timings = timings ?? TaskTimings.Default;
        _watchFiles = watchFiles;
        _sessions = new CodexSessionReader(paths, time, _timings);
        _current = Snapshot([], new ProviderHealth(ProviderHealthState.Starting, "NotStarted", null));
    }

    /// <inheritdoc />
    public AgentProvider Provider => AgentProvider.Codex;

    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;

    /// <inheritdoc />
    public ProviderTaskSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    internal int TrackedFiles => _sessions.TrackedFiles;
    internal long ParseErrorCount => _sessions.ParseErrorCount;
    internal long FormatDriftCount => _sessions.FormatDriftCount;
    internal IReadOnlyDictionary<string, long> FormatDriftBySignature => _sessions.FormatDriftBySignature;
    internal int WatcherOverflowCount => Volatile.Read(ref _watcherOverflowCount);
    internal IReadOnlyList<(string ThreadId, long Offset, long LastReadStartOffset)> ReaderPositions => _sessions.ReaderPositions;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _timer = _time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _ = ReconcileFromSignalAsync(forceDiscovery: true);
    }

    /// <inheritdoc />
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        ReconcileCoreAsync(forceDiscovery: true, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _timer?.Dispose();
            _timer = null;
            _watcher?.Dispose();
            _watcher = null;
        }

        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _sessions.Dispose();
        }
        finally
        {
            _reconcileGate.Release();
            _reconcileGate.Dispose();
            _stop.Dispose();
        }
    }

    async Task ReconcileCoreAsync(bool forceDiscovery, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _reconcileGate.WaitAsync(linked.Token).ConfigureAwait(false);
        ProviderTaskSnapshot next;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            if (!Directory.Exists(_paths.SessionsDirectory))
            {
                DisposeWatcher();
                _watcherUnavailable = false;
                _sessions.Reconcile(forceDiscovery: false);
                next = Snapshot([], new ProviderHealth(ProviderHealthState.Unavailable, "SessionsDirectoryMissing", now));
            }
            else
            {
                EnsureWatcher();
                var tasks = _sessions.Reconcile(forceDiscovery);
                var health = _watcherUnavailable
                    ? new ProviderHealth(ProviderHealthState.Degraded, "WatcherUnavailable", MostRecentEvidence(tasks))
                    : new ProviderHealth(ProviderHealthState.Ok, "Ok", MostRecentEvidence(tasks));
                next = Snapshot(tasks, health);
            }
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            next = Snapshot(Current.Tasks, new ProviderHealth(ProviderHealthState.Degraded, "ReconcileFailed", Current.Health.LastEvidence));
        }
        finally
        {
            _reconcileGate.Release();
        }

        PublishIfChanged(next);
        ScheduleNextTimer();
    }

    void EnsureWatcher()
    {
        if (!_watchFiles || _watcher is not null) return;
        try
        {
            _watcher = new DirectoryWatcher(
                _paths.SessionsDirectory,
                _time,
                _timings.WatcherDebounce,
                OnWatcherChanged,
                OnWatcherOverflow);
            _watcherUnavailable = false;
        }
        catch (IOException)
        {
            _watcherUnavailable = true;
        }
        catch (UnauthorizedAccessException)
        {
            _watcherUnavailable = true;
        }
        catch (ArgumentException)
        {
            _watcherUnavailable = true;
        }
    }

    void DisposeWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    void OnWatcherChanged() => _ = ReconcileFromSignalAsync(forceDiscovery: true);

    void OnWatcherOverflow() => Interlocked.Increment(ref _watcherOverflowCount);

    async Task ReconcileFromSignalAsync(bool forceDiscovery)
    {
        try
        {
            await ReconcileCoreAsync(forceDiscovery, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Watcher callbacks must not terminate the application's file-notification thread.
        }
    }

    void OnTimer(object? state) => _ = RunTimerAsync();

    async Task RunTimerAsync()
    {
        try
        {
            await ReconcileCoreAsync(forceDiscovery: false, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The next timer pass retries; provider health is set by ReconcileCoreAsync.
        }
    }

    void ScheduleNextTimer()
    {
        lock (_gate)
        {
            if (!_started || _disposed || _timer is null) return;
            var hasActiveTask = _current.Tasks.Any(task =>
                task.Status is AgentTaskStatus.Working or AgentTaskStatus.NeedsAttention);
            _timer.Change(hasActiveTask ? ActiveRecheckInterval : _timings.ReconcileInterval, Timeout.InfiniteTimeSpan);
        }
    }

    void PublishIfChanged(ProviderTaskSnapshot next)
    {
        Action<ProviderTaskSnapshot>? handlers;
        lock (_gate)
        {
            if (SnapshotEquals(_current, next)) return;
            _current = next;
            handlers = Changed;
        }

        if (handlers is null) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (subscribers, snapshot) = ((Action<ProviderTaskSnapshot>, ProviderTaskSnapshot))state!;
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try { ((Action<ProviderTaskSnapshot>)subscriber)(snapshot); }
                catch (Exception) { }
            }
        }, (handlers, next));
    }

    static ProviderTaskSnapshot Snapshot(IReadOnlyList<AgentTask> tasks, ProviderHealth health) =>
        new(AgentProvider.Codex, Array.AsReadOnly(tasks.ToArray()), health);

    static DateTimeOffset? MostRecentEvidence(IReadOnlyList<AgentTask> tasks) =>
        tasks.Count == 0 ? null : tasks.Max(task => task.LastActivity);

    static bool SnapshotEquals(ProviderTaskSnapshot left, ProviderTaskSnapshot right) =>
        left.Provider == right.Provider && left.Health == right.Health && left.Tasks.SequenceEqual(right.Tasks);
}
