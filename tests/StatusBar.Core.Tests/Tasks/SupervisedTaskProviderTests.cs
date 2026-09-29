using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class SupervisedTaskProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(50, 300)]
    public void Backoff_doubles_from_five_seconds_to_five_minutes(int failure, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SupervisedTaskProvider.BackoffFor(failure));

    [Fact]
    public async Task Failure_keeps_tasks_marks_degraded_and_restarts_after_backoff()
    {
        var time = new FakeTimeProvider(Now);
        var created = new List<FakeProvider>();
        await using var supervised = new SupervisedTaskProvider(
            AgentProvider.Codex,
            () => { var p = new FakeProvider(AgentProvider.Codex); created.Add(p); return p; },
            time);
        var messages = new List<string>();
        supervised.Supervision += messages.Add;
        supervised.Start();

        var task = CreateTask("codex:a", AgentProvider.Codex);
        created[0].Publish(Ok([task]));
        created[0].Publish(Failed());

        Assert.Equal(ProviderHealthState.Degraded, supervised.Current.Health.State);
        Assert.Equal([task], supervised.Current.Tasks);
        Assert.Single(created);

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(created);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, created.Count);
        Assert.True(created[0].WasDisposed);
        Assert.Equal(1, created[1].StartCount);
        Assert.Equal(SupervisedTaskProvider.RestartingCode, supervised.Current.Health.Code);
        Assert.Equal([task], supervised.Current.Tasks);

        created[1].Publish(Ok([task]));
        Assert.Equal(ProviderHealthState.Ok, supervised.Current.Health.State);
        Assert.Equal(0, supervised.ConsecutiveFailures);
        Assert.Contains(messages, m => m.Contains("restart 1 in 5 s", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_failures_back_off_and_old_provider_events_are_ignored()
    {
        var time = new FakeTimeProvider(Now);
        var created = new List<FakeProvider>();
        await using var supervised = new SupervisedTaskProvider(
            AgentProvider.Claude,
            () => { var p = new FakeProvider(AgentProvider.Claude, startThrows: created.Count > 0); created.Add(p); return p; },
            time);
        supervised.Start();

        created[0].Publish(Failed());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, created.Count);
        Assert.Equal(2, supervised.ConsecutiveFailures);

        // A second failure waits 10 s, not 5 s.
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(2, created.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, created.Count);

        created[0].Publish(Ok([CreateTask("claude:stale", AgentProvider.Claude)]));
        Assert.DoesNotContain(supervised.Current.Tasks, t => t.Id == "claude:stale");
    }

    [Fact]
    public async Task Snapshot_queued_by_a_replaced_provider_is_ignored()
    {
        var time = new FakeTimeProvider(Now);
        var created = new List<FakeProvider>();
        await using var supervised = new SupervisedTaskProvider(
            AgentProvider.Codex,
            () => { var p = new FakeProvider(AgentProvider.Codex); created.Add(p); return p; },
            time);
        supervised.Start();

        // Captured like a thread-pool publish that is already queued when the restart happens.
        var queued = created[0].CaptureHandlers();
        created[0].Publish(Failed());
        time.Advance(SupervisedTaskProvider.InitialBackoff);
        created[1].Publish(Ok([CreateTask("codex:new", AgentProvider.Codex)]));

        queued!(Ok([CreateTask("codex:stale", AgentProvider.Codex)]) with { Provider = AgentProvider.Codex });

        Assert.Equal("codex:new", Assert.Single(supervised.Current.Tasks).Id);
    }

    [Fact]
    public async Task Factory_failure_is_retried_without_throwing()
    {
        var time = new FakeTimeProvider(Now);
        var calls = 0;
        await using var supervised = new SupervisedTaskProvider(
            AgentProvider.Codex,
            () => ++calls == 1 ? throw new IOException() : new FakeProvider(AgentProvider.Codex),
            time);

        supervised.Start();
        Assert.Equal(ProviderHealthState.Degraded, supervised.Current.Health.State);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Provider_that_throws_on_every_reconcile_never_affects_the_other_provider()
    {
        var time = new FakeTimeProvider(Now);
        var codexCreated = 0;
        var codex = new SupervisedTaskProvider(
            AgentProvider.Codex,
            () => { codexCreated++; return new FakeProvider(AgentProvider.Codex, reconcileThrows: true); },
            time);
        var claudeInner = new FakeProvider(AgentProvider.Claude);
        var claude = new SupervisedTaskProvider(AgentProvider.Claude, () => claudeInner, time);
        await using var service = new AgentStateService([codex, claude], time, StateServiceOptions.Default);

        for (var i = 0; i < 12; i++)
        {
            await service.ReconcileAsync();
            claudeInner.Publish(Ok([CreateTask($"claude:{i}", AgentProvider.Claude)]));
            Assert.Equal($"claude:{i}", Assert.Single(service.Current.Tasks).Id);
            Assert.Equal(ProviderHealthState.Ok, service.Current.TaskProviderHealth[AgentProvider.Claude].State);
            Assert.Equal(ProviderHealthState.Degraded, service.Current.TaskProviderHealth[AgentProvider.Codex].State);
            time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.True(codexCreated > 1);
        Assert.Equal(1, claudeInner.StartCount);
        Assert.False(claudeInner.WasDisposed);
    }

    static AgentTask CreateTask(string id, AgentProvider provider) => new()
    {
        Id = id,
        Provider = provider,
        Title = "Placeholder",
        Status = AgentTaskStatus.Working,
        Confidence = StateConfidence.Confirmed,
        LastActivity = Now,
    };

    static ProviderTaskSnapshot Ok(IReadOnlyList<AgentTask> tasks) =>
        new(tasks.FirstOrDefault()?.Provider ?? AgentProvider.Codex, tasks, new ProviderHealth(ProviderHealthState.Ok, "Ok", Now));

    static ProviderTaskSnapshot Failed() =>
        new(AgentProvider.Codex, [], new ProviderHealth(ProviderHealthState.Degraded, SupervisedTaskProvider.ReconcileFailedCode, Now));

    sealed class FakeProvider(AgentProvider provider, bool startThrows = false, bool reconcileThrows = false) : IAgentTaskProvider
    {
        ProviderTaskSnapshot _current = new(provider, [], new ProviderHealth(ProviderHealthState.Starting, "NotStarted", null));

        public AgentProvider Provider { get; } = provider;
        public bool WasDisposed { get; private set; }
        public int StartCount { get; private set; }
        public event Action<ProviderTaskSnapshot>? Changed;
        public ProviderTaskSnapshot Current => _current;

        public void Start()
        {
            StartCount++;
            if (startThrows) throw new InvalidOperationException();
        }

        public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
            reconcileThrows ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }

        public Action<ProviderTaskSnapshot>? CaptureHandlers() => Changed;

        public void Publish(ProviderTaskSnapshot snapshot)
        {
            _current = snapshot with { Provider = Provider };
            Changed?.Invoke(_current);
        }
    }
}
