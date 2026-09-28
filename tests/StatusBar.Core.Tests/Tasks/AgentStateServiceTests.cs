using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class AgentStateServiceTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    [Fact]
    public async Task Merges_providers_orders_tasks_and_counts_working_and_attention()
    {
        var time = new FakeTimeProvider(Now);
        var codex = new TestProvider(AgentProvider.Codex);
        var claude = new TestProvider(AgentProvider.Claude);
        await using var service = new AgentStateService([codex, claude], time, StateServiceOptions.Default);

        codex.Publish(
            TaskSnapshot(AgentProvider.Codex,
            [
                CreateTask("codex:working", AgentProvider.Codex, AgentTaskStatus.Working, Now),
                CreateTask("codex:attention", AgentProvider.Codex, AgentTaskStatus.NeedsAttention, Now.AddMinutes(-1)),
                CreateTask("codex:unknown", AgentProvider.Codex, AgentTaskStatus.Unknown, Now.AddMinutes(-2)),
                CreateTask("codex:complete", AgentProvider.Codex, AgentTaskStatus.Complete, Now.AddMinutes(-3)),
            ]));
        claude.Publish(
            TaskSnapshot(AgentProvider.Claude,
            [
                CreateTask("claude:failed", AgentProvider.Claude, AgentTaskStatus.Failed, Now.AddMinutes(-4)),
                CreateTask("claude:working", AgentProvider.Claude, AgentTaskStatus.Working, Now.AddMinutes(-1)),
            ]));

        Assert.Collection(
            service.Current.Tasks,
            task => Assert.Equal("codex:attention", task.Id),
            task => Assert.Equal("codex:working", task.Id),
            task => Assert.Equal("claude:working", task.Id),
            task => Assert.Equal("codex:unknown", task.Id),
            task => Assert.Equal("codex:complete", task.Id),
            task => Assert.Equal("claude:failed", task.Id));
        Assert.Equal(2, service.Current.WorkingCount);
        Assert.Equal(1, service.Current.AttentionCount);
        Assert.Equal(ProviderHealthState.Ok, service.Current.TaskProviderHealth[AgentProvider.Codex].State);
        Assert.Equal(ProviderHealthState.Ok, service.Current.TaskProviderHealth[AgentProvider.Claude].State);
    }

    [Fact]
    public async Task Completed_and_unknown_tasks_expire_at_their_configured_windows()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Codex);
        var options = new StateServiceOptions(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
        await using var service = new AgentStateService([provider], time, options);
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
        [
            CreateTask("codex:complete", AgentProvider.Codex, AgentTaskStatus.Complete, Now),
            CreateTask("codex:unknown", AgentProvider.Codex, AgentTaskStatus.Unknown, Now),
        ]));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(new[] { "codex:unknown" }, service.Current.Tasks.Select(task => task.Id));

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(service.Current.Tasks);
    }

    [Fact]
    public async Task State_changed_is_not_raised_for_a_visibly_identical_snapshot()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Codex);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var changes = 0;
        service.StateChanged += _ => changes++;
        var task = CreateTask("codex:one", AgentProvider.Codex, AgentTaskStatus.Working, Now);

        provider.Publish(TaskSnapshot(AgentProvider.Codex, [task]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex, [task with { }]));

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Provider_failures_and_disposal_are_isolated()
    {
        var time = new FakeTimeProvider(Now);
        var failing = new TestProvider(
            AgentProvider.Codex,
            start: () => throw new InvalidOperationException(),
            reconcile: _ => Task.FromException(new InvalidOperationException()),
            disposeThrows: true);
        var healthy = new TestProvider(AgentProvider.Claude);
        await using var service = new AgentStateService([failing, healthy], time, StateServiceOptions.Default);

        await service.ReconcileAsync();
        var healthySnapshot = TaskSnapshot(AgentProvider.Claude,
            [CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.Working, Now)]);
        healthy.Publish(healthySnapshot);

        Assert.Equal(ProviderHealthState.Degraded, service.Current.TaskProviderHealth[AgentProvider.Codex].State);
        Assert.Equal(new[] { "claude:one" }, service.Current.Tasks.Select(task => task.Id));
        await service.DisposeAsync();
        Assert.True(failing.WasDisposed);
        Assert.True(healthy.WasDisposed);
    }

    [Fact]
    public async Task Dismissed_evidence_stays_hidden_until_a_new_question_arrives()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Claude);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var attentionEvents = new List<string>();
        service.EnteredNeedsAttention += task => attentionEvents.Add(task.Id);
        var attention = CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.NeedsAttention, Now) with
        {
            Confidence = StateConfidence.Inferred,
            EvidenceKey = "question:one",
        };

        provider.Publish(TaskSnapshot(AgentProvider.Claude, [attention]));
        Assert.Empty(attentionEvents);
        Assert.Single(service.Current.Tasks);

        provider.Publish(TaskSnapshot(AgentProvider.Claude,
            [CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.Working, Now.AddSeconds(1))]));
        provider.Publish(TaskSnapshot(AgentProvider.Claude, [attention with { LastActivity = Now.AddSeconds(2) }]));
        Assert.Empty(attentionEvents);

        service.Dismiss("claude:one");
        Assert.Empty(service.Current.Tasks);
        provider.Publish(TaskSnapshot(AgentProvider.Claude, []));
        provider.Publish(TaskSnapshot(AgentProvider.Claude, [attention with { LastActivity = Now.AddSeconds(3) }]));

        Assert.Empty(service.Current.Tasks);
        Assert.Empty(attentionEvents);

        provider.Publish(TaskSnapshot(AgentProvider.Claude,
            [attention with { EvidenceKey = "question:two", LastActivity = Now.AddSeconds(4) }]));

        Assert.Single(service.Current.Tasks);
        Assert.Equal(new[] { "claude:one" }, attentionEvents);
    }

    [Fact]
    public async Task Unknown_rows_can_be_dismissed_but_confirmed_attention_cannot()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Claude);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var confirmedAttention = CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.NeedsAttention, Now);

        provider.Publish(TaskSnapshot(AgentProvider.Claude, [confirmedAttention]));
        service.Dismiss("claude:one");
        Assert.Single(service.Current.Tasks);

        var unknown = CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.Unknown, Now);
        provider.Publish(TaskSnapshot(AgentProvider.Claude, [unknown]));
        service.Dismiss("claude:one");

        Assert.Empty(service.Current.Tasks);
    }

    [Fact]
    public async Task Demo_provider_cycles_through_the_four_scripted_states()
    {
        var time = new FakeTimeProvider(Now);
        await using var provider = new DemoTaskProvider(AgentProvider.Codex, time);
        provider.Start();
        Assert.Equal(AgentTaskStatus.Working, provider.Current.Tasks[0].Status);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTaskStatus.NeedsAttention, provider.Current.Tasks[0].Status);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTaskStatus.Working, provider.Current.Tasks[0].Status);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTaskStatus.Complete, provider.Current.Tasks[0].Status);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentTaskStatus.Working, provider.Current.Tasks[0].Status);
        await provider.DisposeAsync();
    }

    static AgentTask CreateTask(
        string id,
        AgentProvider provider,
        AgentTaskStatus status,
        DateTimeOffset lastActivity) => new()
    {
        Id = id,
        Provider = provider,
        Title = "Safe demo title",
        Status = status,
        Confidence = StateConfidence.Confirmed,
        LastActivity = lastActivity,
    };

    static ProviderTaskSnapshot TaskSnapshot(AgentProvider provider, IReadOnlyList<AgentTask> tasks) => new(
        provider,
        tasks,
        new ProviderHealth(ProviderHealthState.Ok, "Ready", Now));

    sealed class TestProvider(
        AgentProvider provider,
        Action? start = null,
        Func<CancellationToken, System.Threading.Tasks.Task>? reconcile = null,
        bool disposeThrows = false) : IAgentTaskProvider
    {
        ProviderTaskSnapshot _current = TaskSnapshot(
            provider,
            Array.Empty<AgentTask>());

        public AgentProvider Provider { get; } = provider;
        public bool WasDisposed { get; private set; }
        public event Action<ProviderTaskSnapshot>? Changed;
        public ProviderTaskSnapshot Current => _current;

        public void Start() => start?.Invoke();

        public async System.Threading.Tasks.Task ReconcileAsync(CancellationToken cancellationToken = default)
        {
            if (reconcile is not null) await reconcile(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return disposeThrows
                ? ValueTask.FromException(new InvalidOperationException())
                : ValueTask.CompletedTask;
        }

        public void Publish(ProviderTaskSnapshot snapshot)
        {
            _current = snapshot;
            Changed?.Invoke(snapshot);
        }
    }
}
