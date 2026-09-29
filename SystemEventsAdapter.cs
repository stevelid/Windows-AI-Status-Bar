using System.Net.NetworkInformation;
using Microsoft.Win32;
using StatusBar.Core.Common;

namespace ClaudeUsageWidget;

/// <summary>Forwards Windows resume and network events to <see cref="SystemEventCoordinator"/>.</summary>
/// <remarks>Re-docking on resume and Explorer restart is handled by <see cref="DockController"/>.</remarks>
sealed class SystemEventsAdapter : IDisposable
{
    readonly SystemEventCoordinator _coordinator;
    bool _disposed;

    public SystemEventsAdapter(Action reconcileTasks, Action refreshUsage)
    {
        _coordinator = new SystemEventCoordinator(TimeProvider.System, reconcileTasks, refreshUsage);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _coordinator.Dispose();
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Log.Write("System resumed; reconciling tasks and refreshing usage.");
        _coordinator.OnResume();
    }

    void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Log.Write(e.IsAvailable ? "Network available; refreshing usage." : "Network lost; usage will show as stale.");
        _coordinator.OnNetworkAvailabilityChanged(e.IsAvailable);
    }
}
