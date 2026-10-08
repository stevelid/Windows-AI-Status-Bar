using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Codex;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Codex;

public sealed class CodexCloudTaskProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codex-cloud", name));

    [Fact]
    public void Maps_states_without_using_summaries_or_untrusted_urls()
    {
        var page = CodexCloudTaskParser.Parse(Fixture("page.json"));
        Assert.Equal("example-next-page", page.Cursor);
        Assert.Equal(new[] { AgentTaskStatus.Working, AgentTaskStatus.Complete, AgentTaskStatus.Complete,
            AgentTaskStatus.Failed, AgentTaskStatus.Unknown }, page.Tasks.Select(task => task.Status));
        Assert.All(page.Tasks, task =>
        {
            Assert.True(task.IsCloud);
            Assert.Null(task.AttentionReason);
            Assert.Equal("Cloud", task.StatusDetail);
            Assert.StartsWith("codex-cloud:", task.Id);
        });
        Assert.Equal("https://chatgpt.com/codex/tasks/task_queued", TaskNavigation.CreateLink(page.Tasks[0])!.AbsoluteUri);
        Assert.Equal("Example queued work", page.Tasks[0].Title);
        Assert.Equal(Now.AddMinutes(-1), page.Tasks[1].LastActivity);
    }

    [Theory]
    [InlineData("https://evil.invalid")]
    [InlineData("task_../escape")]
    [InlineData("task_")]
    [InlineData("task_abc?redirect=other")]
    public void Rejects_unusable_cloud_navigation_identities(string id) => Assert.False(CodexCloudTaskParser.IsTaskId(id));

    [Fact]
    public async Task Reads_all_pages_and_keeps_local_tasks_when_cloud_fails_then_recovers()
    {
        var time = new FakeTimeProvider(Now);
        var fail = false;
        var requested = new List<string?>();
        var cloud = new CodexCloudTaskProvider((cursor, _) =>
        {
            requested.Add(cursor);
            if (fail) throw new IOException("Fabricated transport failure");
            return Task.FromResult(Fixture(cursor is null ? "page.json" : "last-page.json"));
        }, time);
        var local = new LocalProvider();
        await using var combined = new CombinedTaskProvider(AgentProvider.Codex, local, cloud);
        await combined.ReconcileAsync();
        Assert.Equal(new string?[] { null, "example-next-page" }, requested);
        Assert.Equal(7, combined.Current.Tasks.Count);
        Assert.Equal(ProviderHealthState.Ok, combined.Current.Health.State);
        fail = true;
        await combined.ReconcileAsync();
        Assert.Equal(7, combined.Current.Tasks.Count);
        Assert.Equal(ProviderHealthState.Degraded, combined.Current.Health.State);
        Assert.Equal(StateConfidence.Confirmed, combined.Current.Tasks.Single(task => !task.IsCloud).Confidence);
        Assert.All(cloud.Current.Tasks, task => Assert.Equal(StateConfidence.Stale, task.Confidence));
        fail = false;
        await combined.ReconcileAsync();
        Assert.Equal(ProviderHealthState.Ok, combined.Current.Health.State);
    }

    [Fact]
    public void Cloud_titles_remain_in_memory_when_history_persistence_is_enabled()
    {
        var file = Path.Combine(Path.GetTempPath(), "cloud-history-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new TaskHistory(new FakeTimeProvider(Now), file, persist: true);
            var task = CodexCloudTaskParser.Parse(Fixture("page.json")).Tasks[1];
            history.Observe([task]);
            Assert.Equal(task.Title, Assert.Single(history.Entries).Title);
            Assert.DoesNotContain(task.Title, File.ReadAllText(file));
            var loaded = new TaskHistory(new FakeTimeProvider(Now), file, persist: true);
            Assert.Equal("Codex Cloud", Assert.Single(loaded.Entries).Title);
            Assert.True(Assert.Single(loaded.Entries).ToTask().IsCloud);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Repeated_cursor_does_not_publish_partial_results()
    {
        await using var cloud = new CodexCloudTaskProvider((_, _) => Task.FromResult(Fixture("page.json")), new FakeTimeProvider(Now));
        await cloud.ReconcileAsync();
        Assert.Empty(cloud.Current.Tasks);
        Assert.Equal(ProviderHealthState.Degraded, cloud.Current.Health.State);
    }

    [Fact]
    public async Task Does_not_overlap_refreshes_and_disposal_cancels_transport()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cloud = new CodexCloudTaskProvider(async (_, token) =>
        {
            calls++;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "";
        }, new FakeTimeProvider(Now));
        var refresh = cloud.ReconcileAsync();
        await started.Task;
        await cloud.ReconcileAsync();
        Assert.Equal(1, calls);
        await cloud.DisposeAsync();
        await refresh;
    }

    [Fact]
    public async Task Periodic_poll_runs_after_thirty_seconds()
    {
        var time = new FakeTimeProvider(Now);
        var calls = 0;
        await using var cloud = new CodexCloudTaskProvider((_, _) =>
        {
            calls++;
            return Task.FromResult(Fixture("last-page.json"));
        }, time);
        cloud.Start();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(28));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
    }

    sealed class LocalProvider : IAgentTaskProvider
    {
        public AgentProvider Provider => AgentProvider.Codex;
        public ProviderTaskSnapshot Current { get; } = new(AgentProvider.Codex,
            [new AgentTask { Id = "codex:example", Provider = AgentProvider.Codex, Title = "Example local work",
                Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed, LastActivity = Now }],
            new ProviderHealth(ProviderHealthState.Ok, "Ok", Now));
        public event Action<ProviderTaskSnapshot>? Changed;
        public void Start() => Changed?.Invoke(Current);
        public Task ReconcileAsync(CancellationToken cancellationToken = default) { Changed?.Invoke(Current); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
