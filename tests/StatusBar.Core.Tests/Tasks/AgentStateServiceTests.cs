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
    public async Task Finishing_a_seen_task_notifies_once_but_recovered_completion_does_not()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Codex);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var finished = new List<AgentTaskStatus>();
        service.TaskFinished += task => finished.Add(task.Status);
        var working = CreateTask("codex:one", AgentProvider.Codex, AgentTaskStatus.Working, Now);
        provider.Publish(TaskSnapshot(AgentProvider.Codex, [working]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.Complete, LastActivity = Now.AddSeconds(1) }]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.Complete, LastActivity = Now.AddSeconds(1) }]));
        Assert.Equal([AgentTaskStatus.Complete], finished);

        provider.Publish(TaskSnapshot(AgentProvider.Codex, [working with { LastActivity = Now.AddSeconds(2) }]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.Complete, StatusDetail = "Stopped", LastActivity = Now.AddSeconds(3) }]));
        Assert.Single(finished);

        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.NeedsAttention, LastActivity = Now.AddSeconds(4) }]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.Failed, LastActivity = Now.AddSeconds(5) }]));
        Assert.Single(finished);

        provider.Publish(TaskSnapshot(AgentProvider.Codex, [working with { LastActivity = Now.AddSeconds(6) }]));
        provider.Publish(TaskSnapshot(AgentProvider.Codex,
            [working with { Status = AgentTaskStatus.Failed, LastActivity = Now.AddSeconds(7) }]));
        Assert.Equal([AgentTaskStatus.Complete, AgentTaskStatus.Failed], finished);
    }

    [Fact]
    public async Task Unknown_and_confirmed_attention_rows_can_be_dismissed()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Claude);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var confirmedAttention = CreateTask("claude:one", AgentProvider.Claude, AgentTaskStatus.NeedsAttention, Now) with
        {
            EvidenceKey = "permission:one",
        };

        provider.Publish(TaskSnapshot(AgentProvider.Claude, [confirmedAttention]));
        service.Dismiss("claude:one");
        Assert.Empty(service.Current.Tasks);

        provider.Publish(TaskSnapshot(AgentProvider.Claude, [confirmedAttention with { LastActivity = Now.AddSeconds(1) }]));
        Assert.Empty(service.Current.Tasks);

        var unknown = confirmedAttention with { Status = AgentTaskStatus.Unknown };
        provider.Publish(TaskSnapshot(AgentProvider.Claude, [unknown]));
        Assert.Single(service.Current.Tasks);
        service.Dismiss("claude:one");

        Assert.Empty(service.Current.Tasks);
    }

    [Theory]
    [InlineData(AgentTaskStatus.Complete)]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Working)]
    public async Task Dismissed_rows_stay_hidden_across_restart_but_new_activity_appears(AgentTaskStatus status)
    {
        var path = Path.Combine(Path.GetTempPath(), "statusbar-row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var stateFile = Path.Combine(path, "state.json");
            var time = new FakeTimeProvider(Now);
            var task = CreateTask("codex:synthetic", AgentProvider.Codex, status, Now);
            var provider = new TestProvider(AgentProvider.Codex);
            await using (var service = new AgentStateService([provider], time, StateServiceOptions.Default,
                new DismissalStore(time, stateFile)))
            {
                provider.Publish(TaskSnapshot(AgentProvider.Codex, [task]));
                service.Dismiss(task.Id);
                provider.Publish(TaskSnapshot(AgentProvider.Codex, [task with { }]));
                Assert.Empty(service.Current.Tasks);
                Assert.Equal(0, service.Current.DoneCount);
                Assert.Equal(0, service.Current.FailedCount);
                Assert.Equal(0, service.Current.WorkingCount);
            }
            var restarted = new TestProvider(AgentProvider.Codex);
            await using var recovered = new AgentStateService([restarted], time, StateServiceOptions.Default,
                new DismissalStore(time, stateFile));
            restarted.Publish(TaskSnapshot(AgentProvider.Codex, [task]));
            Assert.Empty(recovered.Current.Tasks);
            restarted.Publish(TaskSnapshot(AgentProvider.Codex, [task with { LastActivity = Now.AddSeconds(1) }]));
            Assert.Single(recovered.Current.Tasks);
            Assert.DoesNotContain(task.Id, File.ReadAllText(stateFile));
            Assert.DoesNotContain(task.Title, File.ReadAllText(stateFile));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task Dismissing_work_does_not_hide_its_completion_or_suppress_its_finished_notification()
    {
        var time = new FakeTimeProvider(Now);
        var provider = new TestProvider(AgentProvider.Codex);
        await using var service = new AgentStateService([provider], time, StateServiceOptions.Default);
        var finished = 0;
        service.TaskFinished += _ => finished++;
        var task = CreateTask("codex:synthetic", AgentProvider.Codex, AgentTaskStatus.Working, Now) with { EvidenceKey = "turn:one" };
        provider.Publish(TaskSnapshot(AgentProvider.Codex, [task]));
        service.Dismiss(task.Id);
        provider.Publish(TaskSnapshot(AgentProvider.Codex, [task with { Status = AgentTaskStatus.Complete }]));
        Assert.Equal(AgentTaskStatus.Complete, Assert.Single(service.Current.Tasks).Status);
        Assert.Equal(1, finished);
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
