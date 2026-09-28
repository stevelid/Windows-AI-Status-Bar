namespace StatusBar.Core.Tasks;

/// <summary>Publishes a sanitized task that cycles through representative states for UI development.</summary>
public sealed class DemoTaskProvider : IAgentTaskProvider
{
    const int StateCount = 4;

    readonly object _gate = new();
    readonly AgentProvider _provider;
    readonly TimeProvider _time;
    readonly TimeSpan _cycle;
    readonly DateTimeOffset _cycleStart;
    ITimer? _timer;
    ProviderTaskSnapshot _current;
    bool _started;
    bool _disposed;

    /// <inheritdoc />
    public AgentProvider Provider => _provider;

    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;

    /// <inheritdoc />
    public ProviderTaskSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Creates a demo provider with a four-state cycle lasting 20 seconds by default.</summary>
    public DemoTaskProvider(
        AgentProvider provider,
        TimeProvider time,
        TaskTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _provider = provider;
        _time = time;
        _cycle = (timings ?? TaskTimings.Default).DemoCycle;
        if (_cycle <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timings), "DemoCycle must be positive.");

        _cycleStart = time.GetUtcNow();
        _current = BuildSnapshot(_cycleStart);
    }

    /// <inheritdoc />
    public void Start()
    {
        ProviderTaskSnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            snapshot = UpdateSnapshot(_time.GetUtcNow());
            var step = TimeSpan.FromTicks(Math.Max(1, _cycle.Ticks / StateCount));
            _timer = _time.CreateTimer(OnTimer, null, step, step);
        }
        Publish(snapshot);
    }

    /// <inheritdoc />
    public Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderTaskSnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            snapshot = UpdateSnapshot(_time.GetUtcNow());
        }
        Publish(snapshot);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        return ValueTask.CompletedTask;
    }

    void OnTimer(object? state)
    {
        ProviderTaskSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = UpdateSnapshot(_time.GetUtcNow());
        }
        Publish(snapshot);
    }

    ProviderTaskSnapshot UpdateSnapshot(DateTimeOffset now)
    {
        var next = BuildSnapshot(now);
        if (!SnapshotEquals(_current, next)) _current = next;
        return _current;
    }

    ProviderTaskSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var elapsedTicks = (now - _cycleStart).Ticks;
        var cycleTicks = _cycle.Ticks;
        var cyclePosition = ((elapsedTicks % cycleTicks) + cycleTicks) % cycleTicks;
        var stepTicks = Math.Max(1, cycleTicks / StateCount);
        var stepIndex = (int)Math.Min(3, cyclePosition / stepTicks);
        var status = stepIndex switch
        {
            0 => AgentTaskStatus.Working,
            1 => AgentTaskStatus.NeedsAttention,
            2 => AgentTaskStatus.Working,
            _ => AgentTaskStatus.Complete,
        };
        var phaseStart = _cycleStart + TimeSpan.FromTicks(stepTicks * stepIndex);
        var task = new AgentTask
        {
            Id = $"demo:{_provider.ToString().ToLowerInvariant()}",
            Provider = _provider,
            Title = _provider == AgentProvider.Codex ? "Demo Codex task" : "Demo Claude task",
            Status = status,
            Confidence = status == AgentTaskStatus.NeedsAttention
                ? StateConfidence.Inferred
                : StateConfidence.Confirmed,
            LastActivity = phaseStart,
            AttentionReason = status == AgentTaskStatus.NeedsAttention ? "Approval requested" : null,
            StatusDetail = status == AgentTaskStatus.Complete ? "Finished" : null,
        };
        var health = new ProviderHealth(ProviderHealthState.Ok, "Demo", now);
        return new ProviderTaskSnapshot(_provider, Array.AsReadOnly(new[] { task }), health);
    }

    static bool SnapshotEquals(ProviderTaskSnapshot left, ProviderTaskSnapshot right) =>
        left.Provider == right.Provider &&
        left.Health == right.Health &&
        left.Tasks.SequenceEqual(right.Tasks);

    void Publish(ProviderTaskSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (subscribers, changedSnapshot) = ((Action<ProviderTaskSnapshot>, ProviderTaskSnapshot))state!;
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action<ProviderTaskSnapshot>)subscriber)(changedSnapshot);
                }
                catch (Exception)
                {
                    // A subscriber failure must not stop the demo clock.
                }
            }
        }, (handlers, snapshot));
    }
}
