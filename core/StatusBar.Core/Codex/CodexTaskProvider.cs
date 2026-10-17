using StatusBar.Core.IO;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Watches recent Codex rollouts and publishes normalized task snapshots.</summary>
public sealed class CodexTaskProvider : IAgentTaskProvider
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
    DateTimeOffset? _lastEvent;
    CodexTaskDiagnostics _diagnostics;

    /// <summary>Creates a Codex collector using the configured home, CODEX_HOME, or the user profile fallback.</summary>
    /// <param name="homeOverride">Optional settings override for the Codex data directory.</param>
    /// <param name="time">Clock used for reconciliation and task timing.</param>
    /// <param name="timings">Optional task timing rules.</param>
    public CodexTaskProvider(string? homeOverride = null, TimeProvider? time = null, TaskTimings? timings = null)
        : this(
            CodexPaths.Resolve(homeOverride, Environment.GetEnvironmentVariable("CODEX_HOME")),
            time ?? TimeProvider.System,
            timings)
    {
    }

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
        _sessions.Trace += message => Trace?.Invoke(message);
        _sessions.TurnEnded += info => TurnEnded?.Invoke(info);
        _current = Snapshot([], new ProviderHealth(ProviderHealthState.Starting, "NotStarted", null));
        _diagnostics = new CodexTaskDiagnostics(
            _current.Health,
            0,
            0,
            null,
            0,
            0,
            new Dictionary<string, long>(StringComparer.Ordinal),
            0);
    }

    /// <inheritdoc />
    public AgentProvider Provider => AgentProvider.Codex;

    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;

    /// <summary>Raised after a filesystem watcher overflow schedules a full reconciliation.</summary>
    public event Action? WatcherOverflowed;

    /// <summary>Content-free debug lines about sessions and turns (see <c>CodexSessionReader.Trace</c>).</summary>
    public event Action<string>? Trace;

    /// <summary>Raised when a live turn finishes and the optional AI check is on; carries the final message tail.</summary>
    public event StatusBar.Core.Judgment.TurnEndHandler? TurnEnded;

    /// <inheritdoc />
    public ProviderTaskSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Gets a redacted snapshot for the support diagnostics report.</summary>
    public CodexTaskDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                TimeSpan? age = _lastEvent is { } lastEvent
                    ? now >= lastEvent ? now - lastEvent : TimeSpan.Zero
                    : null;
                return _diagnostics with { LastEventAge = age };
            }
        }
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

    /// <summary>
    /// Hands the AI's answer for a finished turn to this provider (null means the rules decide) and
    /// republishes the task. Returns quietly if the turn is no longer the session's latest.
    /// </summary>
    public async Task ApplyTurnJudgmentAsync(StatusBar.Core.Judgment.TurnEndInfo info, StatusBar.Core.Judgment.TurnEndJudgment? judgment)
    {
        ArgumentNullException.ThrowIfNull(info);
        bool applied;
        try
        {
            await _reconcileGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            applied = _sessions.ApplyJudgment(info.TaskKey, info.EvidenceKey, judgment);
        }
        finally
        {
            _reconcileGate.Release();
        }

        if (!applied) return;
        try
        {
            await ReconcileCoreAsync(forceDiscovery: false, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
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
        CodexTaskDiagnostics diagnostics;
        DateTimeOffset? lastEvent;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            if (!Directory.Exists(_paths.SessionsDirectory))
            {
                DisposeWatcher();
                _watcherUnavailable = false;
                var tasks = _sessions.Reconcile(forceDiscovery: false);
                next = Snapshot([], new ProviderHealth(ProviderHealthState.Unavailable, "SessionsDirectoryMissing", now));
                lastEvent = MostRecentEvidence(tasks);
            }
            else
            {
                EnsureWatcher();
                var tasks = _sessions.Reconcile(forceDiscovery);
                var health = _watcherUnavailable
                    ? new ProviderHealth(ProviderHealthState.Degraded, "WatcherUnavailable", MostRecentEvidence(tasks))
                    : new ProviderHealth(ProviderHealthState.Ok, "Ok", MostRecentEvidence(tasks));
                next = Snapshot(tasks, health);
                lastEvent = MostRecentEvidence(tasks);
            }
            diagnostics = BuildDiagnostics(next.Health);
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            next = Snapshot(Current.Tasks, new ProviderHealth(ProviderHealthState.Degraded, "ReconcileFailed", Current.Health.LastEvidence));
            lastEvent = Current.Health.LastEvidence;
            diagnostics = BuildDiagnostics(next.Health);
        }
        finally
        {
            _reconcileGate.Release();
        }

        PublishIfChanged(next, diagnostics, lastEvent);
        ScheduleNextTimer();
    }

    void EnsureWatcher()
    {
        if (!_watchFiles) return;
        lock (_gate)
        {
            if (_disposed || _watcher is not null) return;
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
    }

    void DisposeWatcher()
    {
        DirectoryWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        watcher?.Dispose();
    }

    void OnWatcherChanged() => _ = ReconcileFromSignalAsync(forceDiscovery: true);

    void OnWatcherOverflow()
    {
        Interlocked.Increment(ref _watcherOverflowCount);
        var handlers = WatcherOverflowed;
        if (handlers is null) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            foreach (var subscriber in ((Action)state!).GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch (Exception) { }
            }
        }, handlers);
    }

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

    CodexTaskDiagnostics BuildDiagnostics(ProviderHealth health)
    {
        var trackedFiles = _sessions.TrackedFiles;
        return new CodexTaskDiagnostics(
            health,
            trackedFiles,
            _watchFiles && !_watcherUnavailable ? trackedFiles : 0,
            null,
            _sessions.ParseErrorCount,
            _sessions.FormatDriftCount,
            _sessions.FormatDriftBySignature,
            Volatile.Read(ref _watcherOverflowCount));
    }

    void PublishIfChanged(ProviderTaskSnapshot next, CodexTaskDiagnostics diagnostics, DateTimeOffset? lastEvent)
    {
        Action<ProviderTaskSnapshot>? handlers;
        lock (_gate)
        {
            _diagnostics = diagnostics;
            _lastEvent = lastEvent;
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

    static DateTimeOffset? MostRecentEvidence(IReadOnlyList<AgentTask> tasks)
    {
        if (tasks.Count == 0) return null;
        var latest = tasks.Max(task => task.LastActivity);
        return latest == DateTimeOffset.MinValue ? null : latest;
    }

    static bool SnapshotEquals(ProviderTaskSnapshot left, ProviderTaskSnapshot right) =>
        left.Provider == right.Provider && left.Health == right.Health && left.Tasks.SequenceEqual(right.Tasks);
}
