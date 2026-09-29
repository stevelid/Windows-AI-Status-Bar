using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class NotificationGateTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    [Fact]
    public void First_evaluation_seeds_existing_attention_without_notifying()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        var task = Attention("claude:synthetic-task", "hook:synthetic-one");
        var gate = new NotificationGate(time, statePath);

        gate.Seed([task]);

        Assert.False(gate.ShouldNotify(task));
    }

    [Fact]
    public void Restart_does_not_repeat_a_notification_but_new_evidence_does()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        var first = Attention("codex:synthetic-task", "pending:synthetic-one");
        var second = first with { EvidenceKey = "pending:synthetic-two" };

        var gate = new NotificationGate(time, statePath);
        Assert.True(gate.ShouldNotify(first));

        var restarted = new NotificationGate(time, statePath);
        Assert.False(restarted.ShouldNotify(first));
        Assert.True(restarted.ShouldNotify(second));

        var saved = File.ReadAllText(statePath);
        Assert.DoesNotContain(first.Id, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(first.EvidenceKey!, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Title, saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Notifications_preserve_unrelated_state_sections()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        File.WriteAllText(statePath, "{\"dismissals\":[{\"key\":\"synthetic\"}]}");

        var gate = new NotificationGate(time, statePath);
        Assert.True(gate.ShouldNotify(Attention("claude:synthetic-task", "hook:synthetic-one")));

        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.Single(document.RootElement.GetProperty("dismissals").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("notifications").EnumerateArray());
    }

    static AgentTask Attention(string id, string evidenceKey) => new()
    {
        Id = id,
        Provider = id.StartsWith("codex:", StringComparison.Ordinal)
            ? AgentProvider.Codex
            : AgentProvider.Claude,
        Title = "Synthetic attention title",
        Status = AgentTaskStatus.NeedsAttention,
        Confidence = StateConfidence.Inferred,
        LastActivity = Now,
        AttentionReason = "Synthetic attention",
        EvidenceKey = evidenceKey,
    };

    sealed class TempRoot : IDisposable
    {
        internal TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "statusbar-notification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
