using System.Collections.ObjectModel;

namespace StatusBar.Core.Tasks;

/// <summary>Merges task providers into one immutable, ordered status-bar state.</summary>
public sealed class AgentStateService : IAsyncDisposable
{
    readonly object _gate = new();
    readonly Dictionary<AgentProvider, IAgentTaskProvider> _providers = new();
    readonly Dictionary<AgentProvider, Action<ProviderTaskSnapshot>> _providerHandlers = new();
    readonly Dictionary<AgentProvider, ProviderTaskSnapshot> _snapshots = new();
    readonly HashSet<AgentProvider> _receivedFirstUpdate = new();
    readonly HashSet<string> _dismissedTaskIds = new(StringComparer.Ordinal);
    readonly TimeProvider _time;
    readonly StateServiceOptions _options;
    readonly ITimer _expiryTimer;

    StatusBarState _current = StatusBarState.Empty;
    bool _disposed;

    /// <summary>Raised when tasks or provider health visible to the UI changes.</summary>
    public event Action<StatusBarState>? StateChanged;

    /// <summary>Raised when a task enters NeedsAttention after the provider's initial snapshot.</summary>
    public event Action<AgentTask>? EnteredNeedsAttention;

    /// <summary>The latest merged immutable state.</summary>
    public StatusBarState Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Subscribes to and starts each configured provider.</summary>
    public AgentStateService(
        IEnumerable<IAgentTaskProvider> providers,
        TimeProvider time,
        StateServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RecentlyCompletedFor < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "RecentlyCompletedFor cannot be negative.");
        if (options.UnknownVisibleFor < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "UnknownVisibleFor cannot be negative.");

        var providerList = providers.ToArray();
        foreach (var provider in providerList)
            ArgumentNullException.ThrowIfNull(provider);
        if (providerList.Select(provider => provider.Provider).Distinct().Count() != providerList.Length)
            throw new ArgumentException("Only one task provider can be registered for each provider.", nameof(providers));

        _time = time;
        _options = options;
        _expiryTimer = time.CreateTimer(
            OnExpiryTimer,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        foreach (var provider in providerList)
        {
            _providers.Add(provider.Provider, provider);

            try
            {
                _snapshots.Add(provider.Provider, CopySnapshot(provider.Current, provider.Provider));
            }
            catch (Exception)
            {
                _snapshots.Add(provider.Provider, UnavailableSnapshot(provider.Provider, "ProviderUnavailable"));
            }
        }

        lock (_gate)
        {
            _current = BuildState(_time.GetUtcNow());
            ScheduleNextExpiry(_time.GetUtcNow());
        }

        foreach (var provider in _providers.Values)
        {
            var expectedProvider = provider.Provider;
            Action<ProviderTaskSnapshot> handler = snapshot => OnProviderChanged(expectedProvider, snapshot);
            _providerHandlers.Add(expectedProvider, handler);
            provider.Changed += handler;
        }

        foreach (var provider in _providers.Values)
        {
            try
            {
                provider.Start();
            }
            catch (Exception)
            {
                SetProviderHealth(provider.Provider, new ProviderHealth(
                    ProviderHealthState.Unavailable,
                    "ProviderStartFailed",
                    _time.GetUtcNow()));
            }
        }
    }

    /// <summary>Reconciles each provider independently; one failure leaves the other providers usable.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        IAgentTaskProvider[] providers;
        lock (_gate)
        {
            if (_disposed) return;
            providers = _providers.Values.ToArray();
        }

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await provider.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                OnProviderChanged(provider.Provider, provider.Current);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                SetProviderHealth(provider.Provider, new ProviderHealth(
                    ProviderHealthState.Degraded,
                    "ReconcileFailed",
                    GetProviderHealth(provider.Provider)?.LastEvidence));
            }
        }
    }

    /// <summary>Removes a task from the visible list until its provider clears that task.</summary>
    public void Dismiss(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        StatusBarState? changed;
        lock (_gate)
        {
            if (_disposed || !_current.Tasks.Any(task => task.Id == taskId)) return;
            _dismissedTaskIds.Add(taskId);
            changed = RebuildIfChanged(_time.GetUtcNow());
        }

        if (changed is not null) RaiseStateChanged(changed);
    }

    /// <summary>Cancels expiry timing and disposes every provider without allowing one failure to block another.</summary>
    public async ValueTask DisposeAsync()
    {
        IAgentTaskProvider[] providers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            providers = _providers.Values.ToArray();
            foreach (var provider in providers)
                provider.Changed -= _providerHandlers[provider.Provider];
        }

        _expiryTimer.Dispose();
        foreach (var provider in providers)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A provider's disposal failure must not prevent other collectors from stopping.
            }
        }
    }

    void OnProviderChanged(AgentProvider expectedProvider, ProviderTaskSnapshot snapshot)
    {
        if (snapshot is null) return;
        StatusBarState? changed;
        AgentTask[] entered;
        lock (_gate)
        {
            if (_disposed || !_providers.ContainsKey(expectedProvider)) return;

            var previous = _current;
            try
            {
                _snapshots[expectedProvider] = CopySnapshot(snapshot, expectedProvider);
            }
            catch (Exception)
            {
                _snapshots[expectedProvider] = UnavailableSnapshot(expectedProvider, "InvalidProviderSnapshot");
            }
            var isInitialUpdate = _receivedFirstUpdate.Add(expectedProvider);
            RemoveClearedDismissals();
            changed = RebuildIfChanged(_time.GetUtcNow());
            entered = isInitialUpdate || changed is null
                ? Array.Empty<AgentTask>()
                : _current.Tasks
                    .Where(task => task.Status == AgentTaskStatus.NeedsAttention &&
                        !previous.Tasks.Any(old => old.Id == task.Id && old.Status == AgentTaskStatus.NeedsAttention))
                    .ToArray();
        }

        if (changed is not null) RaiseStateChanged(changed);
        foreach (var task in entered) RaiseEnteredNeedsAttention(task);
    }

    void OnExpiryTimer(object? state)
    {
        StatusBarState? changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = RebuildIfChanged(_time.GetUtcNow());
        }

        if (changed is not null) RaiseStateChanged(changed);
    }

    void SetProviderHealth(AgentProvider provider, ProviderHealth health)
    {
        StatusBarState? changed;
        lock (_gate)
        {
            if (_disposed || !_snapshots.TryGetValue(provider, out var snapshot)) return;
            _snapshots[provider] = snapshot with { Health = health };
            changed = RebuildIfChanged(_time.GetUtcNow());
        }

        if (changed is not null) RaiseStateChanged(changed);
    }

    ProviderHealth? GetProviderHealth(AgentProvider provider)
    {
        lock (_gate)
            return _snapshots.TryGetValue(provider, out var snapshot) ? snapshot.Health : null;
    }

    StatusBarState? RebuildIfChanged(DateTimeOffset now)
    {
        var next = BuildState(now);
        ScheduleNextExpiry(now);
        if (HaveSameVisibleState(_current, next)) return null;
        _current = next;
        return next;
    }

    StatusBarState BuildState(DateTimeOffset now)
    {
        var tasks = _snapshots.Values
            .SelectMany(snapshot => snapshot.Tasks)
            .Where(task => !_dismissedTaskIds.Contains(task.Id) && IsVisible(task, now))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(task => task.LastActivity)
                .ThenBy(task => task.Provider)
                .First())
            .OrderBy(TaskOrder)
            .ThenByDescending(task => task.LastActivity)
            .ThenBy(task => task.Provider)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();
        var health = new ReadOnlyDictionary<AgentProvider, ProviderHealth>(
            _snapshots.ToDictionary(pair => pair.Key, pair => pair.Value.Health));

        return new StatusBarState(
            Array.AsReadOnly(tasks),
            tasks.Count(task => task.Status == AgentTaskStatus.Working),
            tasks.Count(task => task.Status == AgentTaskStatus.NeedsAttention),
            health);
    }

    bool IsVisible(AgentTask task, DateTimeOffset now)
    {
        var age = now - task.LastActivity;
        return task.Status switch
        {
            AgentTaskStatus.Complete or AgentTaskStatus.Failed => age < _options.RecentlyCompletedFor,
            AgentTaskStatus.Unknown => age < _options.UnknownVisibleFor,
            _ => true,
        };
    }

    void ScheduleNextExpiry(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var task in _snapshots.Values.SelectMany(snapshot => snapshot.Tasks))
        {
            var lifetime = task.Status switch
            {
                AgentTaskStatus.Complete or AgentTaskStatus.Failed => _options.RecentlyCompletedFor,
                AgentTaskStatus.Unknown => _options.UnknownVisibleFor,
                _ => TimeSpan.Zero,
            };
            if (lifetime <= TimeSpan.Zero) continue;

            var expiresAt = task.LastActivity + lifetime;
            if (expiresAt <= now) continue;
            if (next is null || expiresAt < next) next = expiresAt;
        }

        var due = next is null ? Timeout.InfiniteTimeSpan : next.Value - now;
        _expiryTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    void RemoveClearedDismissals()
    {
        var activeIds = _snapshots.Values
            .SelectMany(snapshot => snapshot.Tasks)
            .Where(task => task.Status is AgentTaskStatus.NeedsAttention or AgentTaskStatus.Unknown)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        _dismissedTaskIds.RemoveWhere(taskId => !activeIds.Contains(taskId));
    }

    static int TaskOrder(AgentTask task) => task.Status switch
    {
        AgentTaskStatus.NeedsAttention => 0,
        AgentTaskStatus.Working => 1,
        AgentTaskStatus.Unknown => 2,
        AgentTaskStatus.Complete or AgentTaskStatus.Failed => 3,
        _ => 4,
    };

    ProviderTaskSnapshot CopySnapshot(ProviderTaskSnapshot snapshot, AgentProvider expectedProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Provider != expectedProvider)
            return UnavailableSnapshot(expectedProvider, "ProviderMismatch");
        ArgumentNullException.ThrowIfNull(snapshot.Tasks);
        ArgumentNullException.ThrowIfNull(snapshot.Health);
        var tasks = snapshot.Tasks.ToArray();
        if (tasks.Any(task => task is null || task.Provider != expectedProvider))
            return UnavailableSnapshot(expectedProvider, "InvalidTaskProvider");
        return snapshot with { Tasks = Array.AsReadOnly(tasks) };
    }

    ProviderTaskSnapshot UnavailableSnapshot(AgentProvider provider, string code) => new(
        provider,
        Array.Empty<AgentTask>(),
        new ProviderHealth(ProviderHealthState.Unavailable, code, _time.GetUtcNow()));

    static bool HaveSameVisibleState(StatusBarState left, StatusBarState right) =>
        left.WorkingCount == right.WorkingCount &&
        left.AttentionCount == right.AttentionCount &&
        left.Tasks.SequenceEqual(right.Tasks) &&
        left.TaskProviderHealth.Count == right.TaskProviderHealth.Count &&
        left.TaskProviderHealth.All(pair =>
            right.TaskProviderHealth.TryGetValue(pair.Key, out var value) && pair.Value == value);

    void RaiseStateChanged(StatusBarState state)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<StatusBarState>)subscriber)(state);
            }
            catch (Exception)
            {
                // A UI subscriber must not interrupt provider state updates.
            }
        }
    }

    void RaiseEnteredNeedsAttention(AgentTask task)
    {
        var handlers = EnteredNeedsAttention;
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AgentTask>)subscriber)(task);
            }
            catch (Exception)
            {
                // One notification subscriber must not interrupt the others.
            }
        }
    }
}
