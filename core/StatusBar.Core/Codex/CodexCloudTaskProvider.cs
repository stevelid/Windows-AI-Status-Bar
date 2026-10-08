using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Polls cloud tasks independently of local rollout discovery.</summary>
public sealed class CodexCloudTaskProvider : IAgentTaskProvider
{
    readonly Func<string?, CancellationToken, Task<string>> _fetchPage;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _refresh = new(1, 1);
    readonly object _gate = new();
    ITimer? _timer;
    bool _disposed;
    ProviderTaskSnapshot _current = new(AgentProvider.Codex, [],
        new ProviderHealth(ProviderHealthState.Starting, "CloudStarting", null));

    /// <summary>Creates a collector with an injectable page transport and clock.</summary>
    public CodexCloudTaskProvider(Func<string?, CancellationToken, Task<string>> fetchPage, TimeProvider time)
    {
        _fetchPage = fetchPage;
        _time = time;
    }

    /// <inheritdoc />
    public AgentProvider Provider => AgentProvider.Codex;
    /// <inheritdoc />
    public event Action<ProviderTaskSnapshot>? Changed;
    /// <inheritdoc />
    public ProviderTaskSnapshot Current { get { lock (_gate) return _current; } }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer ??= _time.CreateTimer(state => { _ = ReconcileAsync(); }, null,
                TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        try
        {
            if (!await _refresh.WaitAsync(0, token).ConfigureAwait(false)) return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        try
        {
            lock (_gate) { if (_disposed) return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var tasks = new Dictionary<string, AgentTask>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (var pageNumber = 0; ; pageNumber++)
            {
                // Bound a refresh without silently accepting a partial task list.
                if (pageNumber >= 50) throw new InvalidOperationException("Cloud page limit.");
                var page = CodexCloudTaskParser.Parse(await _fetchPage(cursor, timeout.Token).ConfigureAwait(false));
                foreach (var task in page.Tasks)
                    if (!tasks.TryGetValue(task.Id, out var previous) || task.LastActivity > previous.LastActivity)
                        tasks[task.Id] = task;
                cursor = page.Cursor;
                if (cursor is null) break;
                if (!cursors.Add(cursor)) throw new InvalidOperationException("Cloud cursor repeated.");
            }
            Publish(new ProviderTaskSnapshot(Provider, tasks.Values.ToArray(),
                new ProviderHealth(ProviderHealthState.Ok, "CloudOk", _time.GetUtcNow())));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            var previous = Current;
            // Do not keep presenting cached cloud work as confirmed after losing the network.
            Publish(previous with
            {
                Tasks = previous.Tasks.Select(task => task with { Confidence = StateConfidence.Stale }).ToArray(),
                Health = previous.Health with { State = ProviderHealthState.Degraded, Code = "CloudUnavailable" },
            });
        }
        finally { _refresh.Release(); }
    }

    void Publish(ProviderTaskSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _current = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        _refresh.Release();
    }
}
