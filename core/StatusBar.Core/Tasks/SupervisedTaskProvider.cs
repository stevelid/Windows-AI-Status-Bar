namespace StatusBar.Core.Tasks;

/// <summary>
/// Restarts one task provider after an unexpected failure, with back-off, without affecting any other
/// collector. While a restart is pending the last known tasks stay visible with <c>Degraded</c> health.
/// </summary>
public sealed class SupervisedTaskProvider : IAgentTaskProvider
{
    /// <summary>Health code providers publish when a reconcile pass threw unexpectedly.</summary>
    public const string ReconcileFailedCode = "ReconcileFailed";

    /// <summary>Health code published while a replacement provider is being started.</summary>
    public const string RestartingCode = "Restarting";

    /// <summary>First restart delay.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Longest restart delay.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    readonly object _gate = new();
    readonly Func<IAgentTaskProvider> _factory;
    readonly TimeProvider _time;
    readonly ITimer _restartTimer;

    IAgentTaskProvider? _inner;
    Action<ProviderTaskSnapshot>? _innerHandler;
    ProviderTaskSnapshot _current;
    string? _pendingCreateFailure;
    int _consecutiveFailures;
    bool _restartPending;
    bool _started;
    bool _disposed;

    /// <summary>Creates a supervisor; <paramref name="factory"/> is called now and again for each restart.</summary>
    public SupervisedTaskProvider(AgentProvider provider, Func<IAgentTaskProvider> factory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        Provider = provider;
        _factory = factory;
        _time = time;
        _restartTimer = time.CreateTimer(_ => Restart(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _current = new ProviderTaskSnapshot(
            provider,
            Array.Empty<AgentTask>(),
            new ProviderHealth(ProviderHealthState.Starting, "NotStarted", null));

        try
        {
            Attach(CreateInner());
        }
        catch (Exception ex)
        {
            // Start() schedules the first restart; the constructor must not fail the whole app.
            _current = _current with { Health = new ProviderHealth(ProviderHealthState.Degraded, "CreateFailed", null) };
            _pendingCreateFailure = ex.GetType().Name;
        }
    }

    /// <inheritdoc />
    public AgentProvider Provider { get; }

    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;

    /// <summary>Content-free supervision messages (failure code, attempt, delay) for the app log.</summary>
    public event Action<string>? Supervision;

    /// <inheritdoc />
    public ProviderTaskSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Number of consecutive failures since the provider last reported healthy.</summary>
    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    /// <summary>The delay before restart attempt <paramref name="failure"/> (1-based): 5 s doubling to 5 min.</summary>
    public static TimeSpan BackoffFor(int failure)
    {
        if (failure <= 1) return InitialBackoff;
        var ticks = InitialBackoff.Ticks * Math.Pow(2, Math.Min(failure - 1, 16));
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    /// <inheritdoc />
    public void Start()
    {
        IAgentTaskProvider? inner;
        string? createFailure;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            inner = _inner;
            createFailure = _pendingCreateFailure;
            _pendingCreateFailure = null;
        }

        if (inner is null)
        {
            OnFailure(createFailure ?? "CreateFailed");
            return;
        }
        StartInner(inner);
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        IAgentTaskProvider? inner;
        lock (_gate)
        {
            if (_disposed) return;
            inner = _inner;
        }
        if (inner is null) return;

        try
        {
            await inner.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // The inner provider was replaced by a restart while this call was running.
        }
        catch (Exception ex)
        {
            OnFailure(ex.GetType().Name);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IAgentTaskProvider? inner;
        Action<ProviderTaskSnapshot>? handler;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            inner = _inner;
            handler = _innerHandler;
            _inner = null;
            _innerHandler = null;
        }

        _restartTimer.Dispose();
        if (inner is not null)
        {
            if (handler is not null) inner.Changed -= handler;
            await DisposeQuietlyAsync(inner).ConfigureAwait(false);
        }
    }

    IAgentTaskProvider CreateInner()
    {
        var inner = _factory() ?? throw new InvalidOperationException("Provider factory returned null.");
        if (inner.Provider != Provider)
        {
            _ = DisposeQuietlyAsync(inner);
            throw new InvalidOperationException("Provider factory returned the wrong provider.");
        }
        return inner;
    }

    void Attach(IAgentTaskProvider inner)
    {
        Action<ProviderTaskSnapshot> handler = snapshot => OnInnerChanged(inner, snapshot);
        lock (_gate)
        {
            _inner = inner;
            _innerHandler = handler;
        }
        inner.Changed += handler;
    }

    void StartInner(IAgentTaskProvider inner)
    {
        try
        {
            inner.Start();
        }
        catch (Exception ex)
        {
            OnFailure(ex.GetType().Name);
        }
    }

    void OnInnerChanged(IAgentTaskProvider source, ProviderTaskSnapshot snapshot)
    {
        if (snapshot is null) return;
        // Providers publish on the thread pool, so a replaced provider's last snapshot can arrive late.
        lock (_gate)
            if (!ReferenceEquals(source, _inner)) return;
        if (snapshot.Health.State == ProviderHealthState.Degraded &&
            string.Equals(snapshot.Health.Code, ReconcileFailedCode, StringComparison.Ordinal))
        {
            OnFailure(ReconcileFailedCode, snapshot);
            return;
        }

        bool recovered;
        lock (_gate)
        {
            if (_disposed) return;
            recovered = _consecutiveFailures > 0 && snapshot.Health.State != ProviderHealthState.Degraded;
            if (recovered)
            {
                _consecutiveFailures = 0;
                _restartPending = false;
                _restartTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            _current = snapshot;
        }

        if (recovered) RaiseSupervision($"{Provider} provider recovered ({snapshot.Health.Code})");
        RaiseChanged(snapshot);
    }

    void OnFailure(string code, ProviderTaskSnapshot? failedSnapshot = null)
    {
        ProviderTaskSnapshot published;
        TimeSpan delay;
        int attempt;
        lock (_gate)
        {
            if (_disposed) return;
            // Keep the last tasks visible; a failing provider should not make tasks vanish.
            var tasks = failedSnapshot?.Tasks is { Count: > 0 } failedTasks ? failedTasks : _current.Tasks;
            _current = new ProviderTaskSnapshot(
                Provider,
                tasks,
                new ProviderHealth(ProviderHealthState.Degraded, ReconcileFailedCode, _current.Health.LastEvidence));
            published = _current;
            if (_restartPending) return;

            _restartPending = true;
            attempt = ++_consecutiveFailures;
            delay = BackoffFor(attempt);
            _restartTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        RaiseSupervision($"{Provider} provider failed ({code}); restart {attempt} in {delay.TotalSeconds:0} s");
        RaiseChanged(published);
    }

    void Restart()
    {
        IAgentTaskProvider? old;
        Action<ProviderTaskSnapshot>? oldHandler;
        lock (_gate)
        {
            if (_disposed || !_restartPending) return;
            _restartPending = false;
            old = _inner;
            oldHandler = _innerHandler;
            _inner = null;
            _innerHandler = null;
            _current = _current with
            {
                Health = new ProviderHealth(ProviderHealthState.Degraded, RestartingCode, _current.Health.LastEvidence),
            };
        }

        if (old is not null)
        {
            if (oldHandler is not null) old.Changed -= oldHandler;
            _ = DisposeQuietlyAsync(old);
        }

        IAgentTaskProvider replacement;
        try
        {
            replacement = CreateInner();
        }
        catch (Exception ex)
        {
            OnFailure(ex.GetType().Name);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                _ = DisposeQuietlyAsync(replacement);
                return;
            }
        }

        RaiseSupervision($"{Provider} provider restarting");
        Attach(replacement);
        StartInner(replacement);
    }

    static async Task DisposeQuietlyAsync(IAgentTaskProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed provider's disposal must not block its replacement.
        }
    }

    void RaiseChanged(ProviderTaskSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try { ((Action<ProviderTaskSnapshot>)subscriber)(snapshot); }
            catch (Exception) { }
        }
    }

    void RaiseSupervision(string message)
    {
        var handlers = Supervision;
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try { ((Action<string>)subscriber)(message); }
            catch (Exception) { }
        }
    }
}
