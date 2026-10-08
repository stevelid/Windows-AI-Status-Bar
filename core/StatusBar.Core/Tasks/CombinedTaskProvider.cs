namespace StatusBar.Core.Tasks;

/// <summary>Merges independently collected local and cloud tasks for the same application.</summary>
public sealed class CombinedTaskProvider : IAgentTaskProvider
{
    readonly IAgentTaskProvider[] _sources;
    readonly object _gate = new();
    ProviderTaskSnapshot _current;

    /// <summary>Combines collectors without changing the UI's provider identity.</summary>
    public CombinedTaskProvider(AgentProvider provider, params IAgentTaskProvider[] sources)
    {
        if (sources.Length == 0 || sources.Any(source => source.Provider != provider))
            throw new ArgumentException("Sources must belong to the same provider.", nameof(sources));
        Provider = provider;
        _sources = sources;
        _current = BuildSnapshot();
        foreach (var source in sources) source.Changed += OnChanged;
    }

    /// <inheritdoc />
    public AgentProvider Provider { get; }
    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;
    /// <inheritdoc />
    public ProviderTaskSnapshot Current { get { lock (_gate) return _current; } }
    /// <inheritdoc />
    public void Start() { foreach (var source in _sources) source.Start(); }
    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        await Task.WhenAll(_sources.Select(source => source.ReconcileAsync(cancellationToken))).ConfigureAwait(false);

    void OnChanged(ProviderTaskSnapshot _)
    {
        ProviderTaskSnapshot snapshot;
        lock (_gate) _current = snapshot = BuildSnapshot();
        Changed?.Invoke(snapshot);
    }

    ProviderTaskSnapshot BuildSnapshot()
    {
        var snapshots = _sources.Select(source => source.Current).ToArray();
        var unhealthy = snapshots.FirstOrDefault(snapshot => snapshot.Health.State != ProviderHealthState.Ok);
        var health = unhealthy is null ? snapshots[0].Health : unhealthy.Health with
        {
            State = snapshots.Any(snapshot => snapshot.Health.State == ProviderHealthState.Ok)
                ? ProviderHealthState.Degraded : unhealthy.Health.State,
        };
        return new ProviderTaskSnapshot(Provider, snapshots.SelectMany(snapshot => snapshot.Tasks).ToArray(), health);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources) source.Changed -= OnChanged;
        foreach (var source in _sources) await source.DisposeAsync().ConfigureAwait(false);
    }
}
