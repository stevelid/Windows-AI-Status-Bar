namespace StatusBar.Core.Common;

/// <summary>
/// Turns Windows power and network events into collector refreshes. Resume reconciles task
/// providers at once and refreshes usage after the network has had time to come back; a
/// burst of network-available events causes one usage refresh.
/// </summary>
public sealed class SystemEventCoordinator : IDisposable
{
    /// <summary>Delay before the post-resume usage refresh; Wi-Fi usually reconnects within this time.</summary>
    public static readonly TimeSpan ResumeUsageDelay = TimeSpan.FromSeconds(10);

    /// <summary>Network events closer together than this are coalesced into one refresh.</summary>
    public static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(3);

    readonly object _gate = new();
    readonly Action _reconcileTasks;
    readonly Action _refreshUsage;
    readonly ITimer _usageTimer;
    bool _networkLost;
    bool _disposed;

    /// <summary>Creates a coordinator; callbacks run on timer threads and must not block.</summary>
    public SystemEventCoordinator(TimeProvider time, Action reconcileTasks, Action refreshUsage)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(reconcileTasks);
        ArgumentNullException.ThrowIfNull(refreshUsage);
        _reconcileTasks = reconcileTasks;
        _refreshUsage = refreshUsage;
        _usageTimer = time.CreateTimer(_ => Invoke(_refreshUsage), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True between a network-lost event and the next network-available event.</summary>
    public bool NetworkLost
    {
        get { lock (_gate) return _networkLost; }
    }


    /// <summary>The machine resumed from sleep or hibernation.</summary>
    public void OnResume()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _usageTimer.Change(ResumeUsageDelay, Timeout.InfiniteTimeSpan);
        }
        // Session files may have changed while asleep and file watchers can miss events across sleep.
        Invoke(_reconcileTasks);
    }

    /// <summary>Network availability changed. Loss needs no action: failed fetches mark usage Stale.</summary>
    public void OnNetworkAvailabilityChanged(bool available)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _networkLost = !available;
            if (available) _usageTimer.Change(NetworkDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _usageTimer.Dispose();
    }

    void Invoke(Action action)
    {
        lock (_gate)
            if (_disposed) return;
        try
        {
            action();
        }
        catch (Exception)
        {
            // A refresh request must never take down the Windows event thread.
        }
    }
}
