using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Common;

namespace StatusBar.Core.Tests.Common;

public sealed class SystemEventCoordinatorTests
{
    [Fact]
    public void Resume_reconciles_now_and_refreshes_usage_after_the_network_delay()
    {
        var time = new FakeTimeProvider();
        int reconciles = 0, refreshes = 0;
        using var coordinator = new SystemEventCoordinator(time, () => reconciles++, () => refreshes++);

        coordinator.OnResume();
        Assert.Equal(1, reconciles);
        Assert.Equal(0, refreshes);

        time.Advance(SystemEventCoordinator.ResumeUsageDelay);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void Network_events_are_coalesced_and_loss_does_not_refresh()
    {
        var time = new FakeTimeProvider();
        var refreshes = 0;
        using var coordinator = new SystemEventCoordinator(time, () => { }, () => refreshes++);

        coordinator.OnNetworkAvailabilityChanged(false);
        Assert.True(coordinator.NetworkLost);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, refreshes);

        coordinator.OnNetworkAvailabilityChanged(true);
        time.Advance(TimeSpan.FromSeconds(1));
        coordinator.OnNetworkAvailabilityChanged(true);
        time.Advance(TimeSpan.FromSeconds(1));
        coordinator.OnNetworkAvailabilityChanged(true);
        time.Advance(SystemEventCoordinator.NetworkDebounce);

        Assert.Equal(1, refreshes);
        Assert.False(coordinator.NetworkLost);
    }

    [Fact]
    public void Callback_failures_are_contained_and_dispose_stops_pending_refresh()
    {
        var time = new FakeTimeProvider();
        var refreshes = 0;
        var coordinator = new SystemEventCoordinator(time, () => throw new InvalidOperationException(), () => refreshes++);

        coordinator.OnResume();
        coordinator.Dispose();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, refreshes);
    }
}
