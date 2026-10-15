using StatusBar.Core.IO;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Claude;

/// <summary>Watches recent Claude Code transcripts and optional app-owned hook events.</summary>
public sealed class ClaudeCodeTaskProvider : IAgentTaskProvider
{
    readonly object _gate = new();
    readonly SemaphoreSlim _reconcileGate = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly ClaudeCodePaths _paths;
    readonly string? _hookFilePath;
    readonly ClaudeCodeSessionReader _sessions;
    readonly ClaudeHookEventParser? _hooks;
    readonly TimeProvider _time;
    readonly TaskTimings _timings;
    readonly bool _watchFiles;
    readonly Dictionary<string, ClaudeHookEvent> _latestHooks = new(StringComparer.Ordinal);
    ProviderTaskSnapshot _current;
    DirectoryWatcher? _projectsWatcher;
    DirectoryWatcher? _hooksWatcher;
    ITimer? _timer;
    bool _started;
    bool _disposed;
    bool _watcherUnavailable;

    /// <summary>Creates a Claude Code collector using the configured home or profile default.</summary>
    /// <param name="homeOverride">Optional settings override for the Claude Code data directory.</param>
    /// <param name="hookFilePath">Optional app-owned hook event file; pass null when hooks are disabled.</param>
    /// <param name="time">Clock used for reconciliation and task timing.</param>
    /// <param name="timings">Optional task timing rules.</param>
    public ClaudeCodeTaskProvider(
        string? homeOverride = null,
        string? hookFilePath = null,
        TimeProvider? time = null,
        TaskTimings? timings = null)
        : this(
            ClaudeCodePaths.Resolve(homeOverride, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")),
            hookFilePath,
            time ?? TimeProvider.System,
            timings)
    {
    }

    internal ClaudeCodeTaskProvider(
        ClaudeCodePaths paths,
        string? hookFilePath,
        TimeProvider time,
        TaskTimings? timings = null,
        bool watchFiles = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        _paths = paths;
        _hookFilePath = string.IsNullOrWhiteSpace(hookFilePath) ? null : hookFilePath;
        _time = time;
        _timings = timings ?? TaskTimings.Default;
        _watchFiles = watchFiles;
        _sessions = new ClaudeCodeSessionReader(paths, time, _timings);
        _sessions.Trace += message => Trace?.Invoke(message);
        _sessions.TurnEnded += info => TurnEnded?.Invoke(info);
        _hooks = _hookFilePath is null
            ? null
            : new ClaudeHookEventParser(new IncrementalJsonlReader(_hookFilePath), new FormatDriftCounter());
        _current = Snapshot([], new ProviderHealth(ProviderHealthState.Starting, "NotStarted", null));
    }

    /// <inheritdoc />
    public AgentProvider Provider => AgentProvider.Claude;

    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;

    /// <summary>Content-free debug lines about sessions and turns (see <c>ClaudeCodeSessionReader.Trace</c>).</summary>
    public event Action<string>? Trace;

    /// <summary>Raised when a live turn finishes and the optional AI check is on; carries the final message tail.</summary>
    public event StatusBar.Core.Judgment.TurnEndHandler? TurnEnded;

    /// <inheritdoc />
    public ProviderTaskSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    internal int TrackedFiles => _sessions.TrackedFiles;
    internal IReadOnlyList<(string SessionId, long Offset, long LastReadStartOffset)> ReaderPositions => _sessions.ReaderPositions;

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
        }

        DisposeWatchers();
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
            EnsureWatchers();
            var now = _time.GetUtcNow();
            IReadOnlyList<AgentTask> transcripts;
            var transcriptReadFailed = false;
            try
            {
                transcripts = _sessions.Reconcile(forceDiscovery);
            }
            catch (Exception)
            {
                transcriptReadFailed = true;
                transcripts = Current.Tasks
                    .Where(task => task.SessionReference is not null)
                    .ToArray();
            }

            var hookReadFailed = false;
            try
            {
                ReadHookEvents();
            }
            catch (Exception)
            {
                // A broken app-owned hook log must not prevent transcript-only task updates.
                hookReadFailed = true;
            }
            PruneOldHookEvents(now);
            var tasks = CombineEvidence(transcripts, now);
            var health = !Directory.Exists(_paths.ProjectsDirectory)
                ? new ProviderHealth(ProviderHealthState.Unavailable, "ProjectsDirectoryMissing", MostRecentEvidence(tasks))
                : transcriptReadFailed
                    ? new ProviderHealth(ProviderHealthState.Degraded, "TranscriptReadFailed", MostRecentEvidence(tasks))
                    : hookReadFailed
                        ? new ProviderHealth(ProviderHealthState.Degraded, "HookReadFailed", MostRecentEvidence(tasks))
                        : _watcherUnavailable
                    ? new ProviderHealth(ProviderHealthState.Degraded, "WatcherUnavailable", MostRecentEvidence(tasks))
                    : new ProviderHealth(ProviderHealthState.Ok, "Ok", MostRecentEvidence(tasks));
            next = Snapshot(tasks, health);
        }
        catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            next = Snapshot(Current.Tasks, new ProviderHealth(
                ProviderHealthState.Degraded,
                "ReconcileFailed",
                Current.Health.LastEvidence));
        }
        finally
        {
            _reconcileGate.Release();
        }

        PublishIfChanged(next);
        ScheduleNextTimer();
    }

    void ReadHookEvents()
    {
        if (_hooks is null) return;
        // ⚠️ A-K4 Hook events are reliable only when Claude Code runs the configured hooks; the feature remains opt-in.
        foreach (var hookEvent in _hooks.ReadNewEvents())
        {
            if (!_latestHooks.TryGetValue(hookEvent.SessionId, out var current) || hookEvent.Timestamp >= current.Timestamp)
                _latestHooks[hookEvent.SessionId] = hookEvent;
        }
    }

    void PruneOldHookEvents(DateTimeOffset now)
    {
        var oldest = now - _timings.ClaudeCodeRecentFileWindow;
        foreach (var sessionId in _latestHooks
                     .Where(pair => pair.Value.Timestamp < oldest)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _latestHooks.Remove(sessionId);
        }
    }

    IReadOnlyList<AgentTask> CombineEvidence(IReadOnlyList<AgentTask> transcripts, DateTimeOffset now)
    {
        var bySession = transcripts
            .Where(task => !string.IsNullOrWhiteSpace(task.SessionReference))
            .GroupBy(task => task.SessionReference!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(task => task.LastActivity).First(), StringComparer.Ordinal);

        foreach (var hookEvent in _latestHooks.Values)
        {
            bySession.TryGetValue(hookEvent.SessionId, out var transcriptTask);
            var combined = ApplyHookEvent(hookEvent, transcriptTask, now);
            if (combined is not null) bySession[hookEvent.SessionId] = combined;
        }

        return bySession.Values
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(task => task.LastActivity).First())
            .OrderByDescending(task => task.Status == AgentTaskStatus.NeedsAttention)
            .ThenByDescending(task => task.LastActivity)
            .ToArray();
    }

    AgentTask? ApplyHookEvent(ClaudeHookEvent hookEvent, AgentTask? transcriptTask, DateTimeOffset now)
    {
        if (transcriptTask is not null && transcriptTask.LastActivity > hookEvent.Timestamp)
            return transcriptTask;

        var task = transcriptTask ?? NewHookOnlyTask(hookEvent);
        var age = now >= hookEvent.Timestamp ? now - hookEvent.Timestamp : TimeSpan.Zero;
        switch (hookEvent.Event)
        {
            case "UserPromptSubmit":
                if (age >= _timings.ClaudeCodeWorkingUnknownAfter)
                    return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale, LastActivity = hookEvent.Timestamp };
                if (age >= _timings.ClaudeCodeWorkingStaleAfter)
                    return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Stale, AttentionReason = null, StatusDetail = null, LastActivity = hookEvent.Timestamp };
                return task with { Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed, AttentionReason = null, StatusDetail = null, LastActivity = hookEvent.Timestamp };
            case "Notification" when hookEvent.NotificationType == "permission_prompt":
                if (age >= _timings.ClaudeConfirmedAttentionExpiry)
                    return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale, AttentionReason = null, StatusDetail = "No recent activity", LastActivity = hookEvent.Timestamp };
                return task with
                {
                    Status = AgentTaskStatus.NeedsAttention,
                    Confidence = StateConfidence.Confirmed,
                    AttentionReason = "Permission requested",
                    StatusDetail = null,
                    LastActivity = hookEvent.Timestamp,
                };
            case "Notification" when hookEvent.NotificationType == "elicitation_dialog":
                if (age >= _timings.ClaudeConfirmedAttentionExpiry)
                    return task with { Status = AgentTaskStatus.Unknown, Confidence = StateConfidence.Stale, AttentionReason = null, StatusDetail = "No recent activity", LastActivity = hookEvent.Timestamp };
                return task with
                {
                    Status = AgentTaskStatus.NeedsAttention,
                    Confidence = StateConfidence.Confirmed,
                    AttentionReason = "Waiting for your input",
                    StatusDetail = null,
                    LastActivity = hookEvent.Timestamp,
                };
            case "Stop":
            case "SessionEnd":
                if (transcriptTask is { Status: AgentTaskStatus.NeedsAttention } &&
                    StatusBar.Core.Judgment.TurnVerdictPolicy.IsQuestionReason(transcriptTask.AttentionReason))
                    return transcriptTask;
                // ⚠️ A-K6 Stop fires when the turn ends even though background agents are still running.
                if (hookEvent.Event == "Stop" &&
                    transcriptTask is { StatusDetail: ClaudeCodeTaskMapper.WaitingForBackgroundDetail })
                    return transcriptTask;
                return task with
                {
                    Status = AgentTaskStatus.Complete,
                    Confidence = StateConfidence.Confirmed,
                    AttentionReason = null,
                    StatusDetail = null,
                    LastActivity = hookEvent.Timestamp,
                };
            default:
                // Idle and future notifications clear older hook alerts but do not override transcript status.
                return transcriptTask;
        }
    }

    static AgentTask NewHookOnlyTask(ClaudeHookEvent hookEvent) => new()
    {
        Id = "claude:" + hookEvent.SessionId,
        Provider = AgentProvider.Claude,
        Title = "Claude task",
        Status = AgentTaskStatus.Unknown,
        Confidence = StateConfidence.Stale,
        LastActivity = hookEvent.Timestamp,
        SessionReference = hookEvent.SessionId,
    };

    void EnsureWatchers()
    {
        if (!_watchFiles) return;
        var unavailable = false;
        if (Directory.Exists(_paths.ProjectsDirectory))
            EnsureWatcher(ref _projectsWatcher, _paths.ProjectsDirectory, "*.jsonl", includeSubdirectories: true, ref unavailable);
        else
            DisposeWatcher(ref _projectsWatcher);

        if (_hookFilePath is not null)
        {
            var directory = Path.GetDirectoryName(_hookFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                EnsureWatcher(ref _hooksWatcher, directory, Path.GetFileName(_hookFilePath), includeSubdirectories: false, ref unavailable);
            else
                DisposeWatcher(ref _hooksWatcher);
        }
        else
        {
            DisposeWatcher(ref _hooksWatcher);
        }

        _watcherUnavailable = unavailable;
    }

    void EnsureWatcher(ref DirectoryWatcher? watcher, string directory, string filter, bool includeSubdirectories, ref bool unavailable)
    {
        lock (_gate)
        {
            if (_disposed || watcher is not null) return;
            try
            {
                watcher = new DirectoryWatcher(
                    directory,
                    _time,
                    _timings.WatcherDebounce,
                    OnWatcherChanged,
                    OnWatcherOverflow,
                    filter,
                    includeSubdirectories);
            }
            catch (IOException)
            {
                unavailable = true;
            }
            catch (UnauthorizedAccessException)
            {
                unavailable = true;
            }
            catch (ArgumentException)
            {
                unavailable = true;
            }
            catch (InvalidOperationException)
            {
                unavailable = true;
            }
        }
    }

    void DisposeWatcher(ref DirectoryWatcher? watcher)
    {
        DirectoryWatcher? current;
        lock (_gate)
        {
            current = watcher;
            watcher = null;
        }
        current?.Dispose();
    }

    void DisposeWatchers()
    {
        DisposeWatcher(ref _projectsWatcher);
        DisposeWatcher(ref _hooksWatcher);
    }

    void OnWatcherChanged() => _ = ReconcileFromSignalAsync(forceDiscovery: true);
    void OnWatcherOverflow() => _ = ReconcileFromSignalAsync(forceDiscovery: true);

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
            _timer.Change(_timings.ReconcileInterval, Timeout.InfiniteTimeSpan);
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
        new(AgentProvider.Claude, Array.AsReadOnly(tasks.ToArray()), health);

    static DateTimeOffset? MostRecentEvidence(IReadOnlyList<AgentTask> tasks)
    {
        if (tasks.Count == 0) return null;
        var latest = tasks.Max(task => task.LastActivity);
        return latest == DateTimeOffset.MinValue ? null : latest;
    }

    static bool SnapshotEquals(ProviderTaskSnapshot left, ProviderTaskSnapshot right) =>
        left.Provider == right.Provider && left.Health == right.Health && left.Tasks.SequenceEqual(right.Tasks);
}
