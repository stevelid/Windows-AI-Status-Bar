using System.Collections.Concurrent;
using StatusBar.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace StatusBar.Core.Tests.Usage;

public class UsageMonitorTests
{
    static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(90);
    static readonly IReadOnlyList<UsageWindow> SampleWindows =
    [
        new UsageWindow("session", "Session", 25, null),
    ];

    [Fact]
    public async Task Start_fetches_each_provider_and_one_failure_does_not_stop_the_other()
    {
        var failing = new TestProvider(
            UsageSource.Codex,
            (_, _) => Task.FromException<IReadOnlyList<UsageWindow>>(new InvalidOperationException()));
        var healthy = new TestProvider(
            UsageSource.Claude,
            (_, _) => Task.FromResult(SampleWindows));
        await using var monitor = NewMonitor([failing, healthy], new FakeTimeProvider());
        var failedSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Codex, s => s.Health == UsageHealth.Unavailable);
        var healthySnapshot = WaitForSnapshotAsync(monitor, UsageSource.Claude, s => s.Health == UsageHealth.Ok);

        monitor.Start();
        var results = await Task.WhenAll(failedSnapshot, healthySnapshot);

        Assert.Equal(UsageHealth.Unavailable, results[0].Health);
        Assert.Equal(UsageHealth.Ok, results[1].Health);
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task Failure_keeps_the_last_good_windows_and_marks_the_snapshot_stale()
    {
        var provider = new TestProvider(
            UsageSource.Codex,
            (call, _) => call == 1
                ? Task.FromResult(SampleWindows)
                : Task.FromException<IReadOnlyList<UsageWindow>>(new InvalidOperationException()));
        var time = new FakeTimeProvider();
        await using var monitor = NewMonitor([provider], time);
        var firstSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Codex, s => s.Health == UsageHealth.Ok);

        monitor.Start();
        var lastGood = await firstSnapshot;
        await AllowLoopToScheduleAsync();

        var failureUpdate = WaitForSnapshotChangeAsync(monitor, UsageSource.Codex, lastGood);
        time.Advance(BaseInterval);
        var stale = await failureUpdate;

        Assert.Equal(2, provider.Calls);
        Assert.Equal(UsageHealth.Stale, stale.Health);
        Assert.Equal("RefreshFailed", stale.StatusCode);
        Assert.Equal(lastGood.Windows, stale.Windows);
        Assert.Equal(lastGood.LastSuccess, stale.LastSuccess);
    }

    [Fact]
    public async Task General_failures_back_off_from_90_to_180_to_360_and_cap_at_600_seconds()
    {
        var provider = new TestProvider(
            UsageSource.Codex,
            (_, _) => Task.FromException<IReadOnlyList<UsageWindow>>(new InvalidOperationException()));
        var time = new FakeTimeProvider();
        await using var monitor = NewMonitor([provider], time);
        var firstSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Codex, s => s.Health == UsageHealth.Unavailable);

        monitor.Start();
        await firstSnapshot;
        await AllowLoopToScheduleAsync();

        var calls = 1;
        foreach (var delaySeconds in new[] { 90, 180, 360, 600 })
        {
            var before = monitor.Current[UsageSource.Codex];
            var nextFailure = WaitForSnapshotChangeAsync(monitor, UsageSource.Codex, before);
            time.Advance(TimeSpan.FromSeconds(delaySeconds - 1));
            await Task.Delay(10);
            Assert.Equal(calls, provider.Calls);

            time.Advance(TimeSpan.FromSeconds(1));
            await provider.WaitForCallsAsync(calls + 1);
            await nextFailure;
            calls++;
            await AllowLoopToScheduleAsync();
        }

        Assert.Equal(5, provider.Calls);
    }

    [Theory]
    [InlineData(15, 90)]
    [InlineData(120, 120)]
    [InlineData(900, 600)]
    public async Task Rate_limit_retry_after_is_honoured_and_clamped(int retryAfterSeconds, int expectedDelaySeconds)
    {
        var provider = new TestProvider(
            UsageSource.Claude,
            (call, _) => call == 1
                ? Task.FromException<IReadOnlyList<UsageWindow>>(
                    new UsageRateLimitedException(TimeSpan.FromSeconds(retryAfterSeconds)))
                : Task.FromResult(SampleWindows));
        var time = new FakeTimeProvider();
        await using var monitor = NewMonitor([provider], time);
        var rateLimited = WaitForSnapshotAsync(monitor, UsageSource.Claude, s => s.StatusCode == "RateLimited");

        monitor.Start();
        await rateLimited;
        await AllowLoopToScheduleAsync();

        var ready = WaitForSnapshotChangeAsync(monitor, UsageSource.Claude, monitor.Current[UsageSource.Claude]);
        time.Advance(TimeSpan.FromSeconds(expectedDelaySeconds - 1));
        await Task.Delay(10);
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        await provider.WaitForCallsAsync(2);
        Assert.Equal(UsageHealth.Ok, (await ready).Health);
    }

    [Fact]
    public async Task Sign_in_required_retains_windows_and_retries_at_the_base_interval()
    {
        var provider = new TestProvider(
            UsageSource.Claude,
            (call, _) => call switch
            {
                1 => Task.FromResult(SampleWindows),
                2 => Task.FromException<IReadOnlyList<UsageWindow>>(new UsageAuthRequiredException("Sign in")),
                _ => Task.FromResult(SampleWindows),
            });
        var time = new FakeTimeProvider();
        await using var monitor = NewMonitor([provider], time);
        var firstSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Claude, s => s.Health == UsageHealth.Ok);

        monitor.Start();
        var lastGood = await firstSnapshot;
        await AllowLoopToScheduleAsync();

        var signedOutUpdate = WaitForSnapshotChangeAsync(monitor, UsageSource.Claude, lastGood);
        monitor.RefreshNow(UsageSource.Claude);
        var signedOut = await signedOutUpdate;
        Assert.Equal(UsageHealth.SignedOut, signedOut.Health);
        Assert.Equal(lastGood.Windows, signedOut.Windows);
        Assert.Equal(lastGood.LastSuccess, signedOut.LastSuccess);
        await AllowLoopToScheduleAsync();

        var ready = WaitForSnapshotChangeAsync(monitor, UsageSource.Claude, signedOut);
        time.Advance(BaseInterval - TimeSpan.FromSeconds(1));
        await Task.Delay(10);
        Assert.Equal(2, provider.Calls);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(UsageHealth.Ok, (await ready).Health);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task Refresh_now_is_immediate_and_coalesces_while_pending_or_in_flight()
    {
        var firstFetchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFetch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestProvider(UsageSource.Codex, async (call, cancellationToken) =>
        {
            if (call == 1)
            {
                firstFetchEntered.TrySetResult(true);
                await releaseFirstFetch.Task.WaitAsync(cancellationToken);
            }
            return SampleWindows;
        });
        await using var monitor = NewMonitor([provider], new FakeTimeProvider());
        var firstSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Codex, s => s.Health == UsageHealth.Ok);

        monitor.Start();
        await firstFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.RefreshNow(UsageSource.Codex);
        monitor.RefreshNow(UsageSource.Codex);
        releaseFirstFetch.TrySetResult(true);
        await firstSnapshot;
        await AllowLoopToScheduleAsync();
        Assert.Equal(1, provider.Calls);

        var refreshed = WaitForSnapshotChangeAsync(monitor, UsageSource.Codex, monitor.Current[UsageSource.Codex]);
        monitor.RefreshNow(UsageSource.Codex);
        monitor.RefreshNow(UsageSource.Codex);
        await provider.WaitForCallsAsync(2);
        Assert.Equal(UsageHealth.Ok, (await refreshed).Health);
        await AllowLoopToScheduleAsync();
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Dispose_cancels_the_refresh_loop()
    {
        var provider = new TestProvider(UsageSource.Codex, (_, _) => Task.FromResult(SampleWindows));
        var time = new FakeTimeProvider();
        var monitor = NewMonitor([provider], time);
        var firstSnapshot = WaitForSnapshotAsync(monitor, UsageSource.Codex, s => s.Health == UsageHealth.Ok);

        monitor.Start();
        await firstSnapshot;
        await AllowLoopToScheduleAsync();
        await monitor.DisposeAsync();

        time.Advance(TimeSpan.FromMinutes(20));
        await Task.Delay(10);

        Assert.Equal(1, provider.Calls);
    }

    static UsageMonitor NewMonitor(IEnumerable<IUsageProvider> providers, TimeProvider time) =>
        new(providers, time, () => BaseInterval);

    static async Task<UsageSnapshot> WaitForSnapshotAsync(
        UsageMonitor monitor,
        UsageSource source,
        Func<UsageSnapshot, bool> predicate)
    {
        var completion = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(UsageSnapshot snapshot)
        {
            if (snapshot.Source == source && predicate(snapshot)) completion.TrySetResult(snapshot);
        }

        monitor.Changed += OnChanged;
        try
        {
            var current = monitor.Current[source];
            if (predicate(current)) return current;
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            monitor.Changed -= OnChanged;
        }
    }

    static Task<UsageSnapshot> WaitForSnapshotChangeAsync(
        UsageMonitor monitor,
        UsageSource source,
        UsageSnapshot previous) =>
        WaitForSnapshotAsync(monitor, source, snapshot => !ReferenceEquals(snapshot, previous));

    static Task AllowLoopToScheduleAsync() => Task.Delay(20);

    sealed class TestProvider(
        UsageSource source,
        Func<int, CancellationToken, Task<IReadOnlyList<UsageWindow>>> fetch) : IUsageProvider
    {
        readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _callWaiters = new();
        int _calls;

        public UsageSource Source { get; } = source;
        public int Calls => Volatile.Read(ref _calls);

        public Task<IReadOnlyList<UsageWindow>> FetchAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            foreach (var waiter in _callWaiters.ToArray())
            {
                if (waiter.Key <= call && _callWaiters.TryRemove(waiter.Key, out var completion))
                    completion.TrySetResult(true);
            }
            return fetch(call, cancellationToken);
        }

        public async Task WaitForCallsAsync(int expected)
        {
            if (Calls >= expected) return;
            var completion = _callWaiters.GetOrAdd(
                expected,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Calls >= expected) completion.TrySetResult(true);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
