using System.Collections.ObjectModel;

namespace StatusBar.Core.Usage;

/// <summary>Runs an independent refresh loop for each usage provider.</summary>
public sealed class UsageMonitor : IAsyncDisposable
{
    static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(600);

    readonly object _lifecycleGate = new();
    readonly object _snapshotGate = new();
    readonly Dictionary<UsageSource, ProviderRuntime> _runtimes = new();
    readonly TimeProvider _time;
    readonly Func<TimeSpan> _baseInterval;
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _current;
    bool _started;
    bool _disposed;

    /// <summary>Raised on a thread-pool thread after a provider snapshot changes.</summary>
    public event Action<UsageSnapshot>? Changed;

    /// <summary>The latest immutable snapshot for each configured provider.</summary>
    public IReadOnlyDictionary<UsageSource, UsageSnapshot> Current
    {
        get
        {
            lock (_snapshotGate) return _current;
        }
    }

    /// <summary>Creates a monitor with one initial Loading snapshot per provider.</summary>
    public UsageMonitor(IEnumerable<IUsageProvider> providers, TimeProvider time, Func<TimeSpan> baseInterval)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(baseInterval);

        _time = time;
        _baseInterval = baseInterval;
        _ = ReadBaseInterval();

        var initial = new Dictionary<UsageSource, UsageSnapshot>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var source = provider.Source;
            if (_runtimes.ContainsKey(source))
                throw new ArgumentException($"Only one usage provider can be registered for {source}.", nameof(providers));

            _runtimes.Add(source, new ProviderRuntime(provider));
            initial.Add(source, new UsageSnapshot(
                source,
                Array.Empty<UsageWindow>(),
                UsageHealth.Loading,
                null,
                "Loading"));
        }

        _current = new ReadOnlyDictionary<UsageSource, UsageSnapshot>(initial);
    }

    /// <summary>Starts each provider's independent loop. Repeated calls have no effect.</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UsageMonitor));
            if (_started) return;

            _started = true;
            foreach (var runtime in _runtimes.Values)
                runtime.LoopTask = Task.Run(() => RunProviderLoopAsync(runtime, runtime.Cancellation.Token));
        }
    }

    /// <summary>Requests an immediate refresh. Requests coalesce while a fetch is pending or in flight.</summary>
    public void RefreshNow(UsageSource? source = null)
    {
        ProviderRuntime[] targets;
        lock (_lifecycleGate)
        {
            if (!_started || _disposed) return;
            if (source is { } selected)
            {
                if (!_runtimes.TryGetValue(selected, out var runtime)) return;
                targets = [runtime];
            }
            else
            {
                targets = _runtimes.Values.ToArray();
            }
        }

        foreach (var runtime in targets)
        {
            lock (runtime.RefreshGate)
            {
                if (!runtime.IsFetching) runtime.RefreshSignal.TrySetResult(true);
            }
        }
    }

    /// <summary>Cancels and waits for all provider loops.</summary>
    public async ValueTask DisposeAsync()
    {
        ProviderRuntime[] runtimes;
        Task[] loops;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            runtimes = _runtimes.Values.ToArray();
            loops = runtimes
                .Select(runtime => runtime.LoopTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        foreach (var runtime in runtimes)
        {
            try
            {
                runtime.Cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // One provider's cancellation callback must not prevent the others from stopping.
            }
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        finally
        {
            foreach (var runtime in runtimes) runtime.Cancellation.Dispose();
        }
    }

    async Task RunProviderLoopAsync(ProviderRuntime runtime, CancellationToken cancellationToken)
    {
        var backoff = ReadBaseInterval();
        var nextDelay = TimeSpan.Zero;
        var firstFetch = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!firstFetch)
                    await WaitForNextFetchAsync(runtime, nextDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            firstFetch = false;
            BeginFetch(runtime);
            try
            {
                var windows = await runtime.Provider.FetchAsync(cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(windows);

                Publish(new UsageSnapshot(
                    runtime.Provider.Source,
                    Array.AsReadOnly(windows.ToArray()),
                    UsageHealth.Ok,
                    _time.GetUtcNow(),
                    "Ready"));

                runtime.QuickRetriesLeft = 0;

                backoff = ReadBaseInterval();
                nextDelay = backoff;
            }
            catch (UsageRateLimitedException ex)
            {
                var current = ReadSnapshot(runtime.Provider.Source);
                Publish(current with { Health = UsageHealth.Stale, StatusCode = "RateLimited" });
                nextDelay = ClampDelay(ex.RetryAfter ?? backoff, ReadBaseInterval());
            }
            catch (UsageAuthRequiredException)
            {
                var current = ReadSnapshot(runtime.Provider.Source);
                Publish(current with { Health = UsageHealth.SignedOut, StatusCode = "AuthRequired" });
                backoff = ReadBaseInterval();
                nextDelay = backoff;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var current = ReadSnapshot(runtime.Provider.Source);
                var health = current.Windows.Count > 0 ? UsageHealth.Stale : UsageHealth.Unavailable;
                Publish(current with
                {
                    Health = health,
                    StatusCode = health == UsageHealth.Stale ? "RefreshFailed" : "Unavailable",
                    // Type name only: exception messages can contain account or network details.
                    ErrorType = ex.GetType().Name,
                });

                if (health == UsageHealth.Unavailable && runtime.QuickRetriesLeft > 0)
                {
                    // A provider that has never loaded retries quickly, so one transient failure
                    // at start-up does not leave the strip blank for a full refresh interval.
                    runtime.QuickRetriesLeft--;
                    nextDelay = QuickRetryDelay;
                }
                else
                {
                    var baseInterval = ReadBaseInterval();
                    nextDelay = ClampDelay(backoff, baseInterval);
                    backoff = DoubleBackoff(nextDelay, baseInterval);
                }
            }
            finally
            {
                EndFetch(runtime);
            }
        }
    }

    async Task WaitForNextFetchAsync(
        ProviderRuntime runtime,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        Task refreshSignal;
        lock (runtime.RefreshGate) refreshSignal = runtime.RefreshSignal.Task;

        var delayTask = Task.Delay(delay, _time, cancellationToken);
        var completed = await Task.WhenAny(delayTask, refreshSignal).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == delayTask) await delayTask.ConfigureAwait(false);
        else await refreshSignal.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    static void BeginFetch(ProviderRuntime runtime)
    {
        lock (runtime.RefreshGate)
        {
            runtime.IsFetching = true;
            runtime.RefreshSignal = NewRefreshSignal();
        }
    }

    static void EndFetch(ProviderRuntime runtime)
    {
        lock (runtime.RefreshGate) runtime.IsFetching = false;
    }

    UsageSnapshot ReadSnapshot(UsageSource source)
    {
        lock (_snapshotGate) return _current[source];
    }

    void Publish(UsageSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            var updated = new Dictionary<UsageSource, UsageSnapshot>(_current)
            {
                [snapshot.Source] = snapshot,
            };
            _current = new ReadOnlyDictionary<UsageSource, UsageSnapshot>(updated);
        }

        var handlers = Changed;
        if (handlers is null) return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (subscribers, changedSnapshot) = ((Action<UsageSnapshot>, UsageSnapshot))state!;
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action<UsageSnapshot>)subscriber)(changedSnapshot);
                }
                catch (Exception)
                {
                    // A UI subscriber must not stop this provider's refresh loop or another subscriber.
                }
            }
        }, (handlers, snapshot));
    }

    TimeSpan ReadBaseInterval()
    {
        var interval = _baseInterval();
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(_baseInterval), "The base interval must be positive.");

        return interval > MaximumBackoff ? MaximumBackoff : interval;
    }

    static TimeSpan ClampDelay(TimeSpan delay, TimeSpan baseInterval)
    {
        if (delay < baseInterval) return baseInterval;
        return delay > MaximumBackoff ? MaximumBackoff : delay;
    }

    static TimeSpan DoubleBackoff(TimeSpan current, TimeSpan baseInterval)
    {
        var bounded = ClampDelay(current, baseInterval);
        return bounded.Ticks >= MaximumBackoff.Ticks / 2
            ? MaximumBackoff
            : TimeSpan.FromTicks(bounded.Ticks * 2);
    }

    static TaskCompletionSource<bool> NewRefreshSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static readonly TimeSpan QuickRetryDelay = TimeSpan.FromSeconds(15);
    const int InitialQuickRetries = 2;

    sealed class ProviderRuntime(IUsageProvider provider)
    {
        public IUsageProvider Provider { get; } = provider;
        public CancellationTokenSource Cancellation { get; } = new();
        public object RefreshGate { get; } = new();
        public TaskCompletionSource<bool> RefreshSignal { get; set; } = NewRefreshSignal();
        public bool IsFetching { get; set; }
        public Task? LoopTask { get; set; }
        public int QuickRetriesLeft { get; set; } = InitialQuickRetries;
    }
}
