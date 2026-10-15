using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Sync;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class LiveTaskExchangeTests
{
    const string Sender = "00000000000040008000000000000001";
    const string Recipient = "00000000000040008000000000000002";
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    static AgentTask Task(AgentTaskStatus status = AgentTaskStatus.Working) => new()
    {
        Id = "claude:sample", Provider = AgentProvider.Claude, Title = "Fictional sample task", Status = status,
        Confidence = StateConfidence.Confirmed, LastActivity = Now,
        SessionReference = "sample-session-reference", AttentionReason = "Permission requested",
    };
    static LiveTaskSnapshot Snapshot(long sequence = 1) => LiveTaskExchange.Create(Sender, "Office", sequence, Now, [Task()]);

    [Fact]
    public void Export_has_only_display_fields_and_excludes_remote_and_cloud_tasks()
    {
        var snapshot = LiveTaskExchange.Create(Sender, "Office", 1, Now,
            [Task(), Task() with { IsRemote = true }, Task() with { IsCloud = true }]);
        Assert.Single(snapshot.Tasks);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("sample-session-reference", json);
        Assert.DoesNotContain("Permission requested", json);
        Assert.Equal(snapshot.Tasks[0], LiveTaskExchange.Read(Encoding.UTF8.GetBytes(json)).Tasks[0]);
    }

    [Fact]
    public void Encrypted_round_trip_hides_titles_and_requires_the_right_recipient_and_sender()
    {
        using var sender = RSA.Create(2048);
        using var recipient = RSA.Create(2048);
        using var stranger = RSA.Create(2048);
        var encrypted = LiveTaskEncryption.Seal(Snapshot(), Recipient, sender, recipient);
        Assert.DoesNotContain("Fictional sample task", Encoding.UTF8.GetString(encrypted));
        Assert.Equal("Fictional sample task", LiveTaskEncryption.Open(encrypted, Sender, Recipient, sender, recipient).Tasks[0].Title);
        Assert.ThrowsAny<Exception>(() => LiveTaskEncryption.Open(encrypted, Sender, Recipient, stranger, recipient));
        Assert.ThrowsAny<Exception>(() => LiveTaskEncryption.Open(encrypted, Sender, Recipient, sender, stranger));
        Assert.ThrowsAny<Exception>(() => LiveTaskEncryption.Open(encrypted, Sender, Sender, sender, recipient));
        var corrupt = System.Text.Json.Nodes.JsonNode.Parse(encrypted)!;
        corrupt["Payload"] = "AAAA";
        Assert.ThrowsAny<Exception>(() => LiveTaskEncryption.Open(JsonSerializer.SerializeToUtf8Bytes(corrupt), Sender, Recipient, sender, recipient));
    }

    [Fact]
    public void Duplicate_and_reordered_snapshots_cannot_keep_a_missing_host_alive()
    {
        var time = new FakeTimeProvider(Now);
        var inbox = new LiveTaskInbox(time);
        Assert.True(inbox.Accept(Snapshot()));
        Assert.False(Assert.Single(inbox.Current()).IsFresh);
        Assert.Equal(AgentTaskStatus.Unknown, Assert.Single(Assert.Single(inbox.Current()).Tasks).Status);
        Assert.True(inbox.Accept(Snapshot(2)));
        Assert.True(Assert.Single(inbox.Current()).IsFresh);
        Assert.Equal(AgentTaskStatus.Working, Assert.Single(Assert.Single(inbox.Current()).Tasks).Status);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(inbox.Accept(Snapshot(2)));
        Assert.False(inbox.Accept(Snapshot(1)));
        Assert.False(Assert.Single(inbox.Current()).IsFresh);
        Assert.Equal(AgentTaskStatus.Unknown, Assert.Single(Assert.Single(inbox.Current()).Tasks).Status);
        Assert.True(inbox.Accept(Snapshot(3)));
        Assert.True(Assert.Single(inbox.Current()).IsFresh);
        time.Advance(LiveTaskInbox.ForgetAfter);
        Assert.Empty(inbox.Current());
    }

    [Fact]
    public void Receiver_clock_controls_freshness_and_restart_requires_new_evidence()
    {
        var time = new FakeTimeProvider(Now);
        var inbox = new LiveTaskInbox(time);
        inbox.Accept(Snapshot() with { PublishedAt = Now.AddYears(10) });
        inbox.Accept(Snapshot(2) with { PublishedAt = Now.AddYears(-10) });
        Assert.True(Assert.Single(inbox.Current()).IsFresh);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(Assert.Single(inbox.Current()).IsFresh);
        var restarted = new LiveTaskInbox(time);
        restarted.Accept(Snapshot(2));
        Assert.False(Assert.Single(restarted.Current()).IsFresh);
    }

    [Fact]
    public void Remote_rows_affect_counts_without_entering_local_history_projects_or_navigation()
    {
        var inbox = new LiveTaskInbox(new FakeTimeProvider(Now));
        inbox.Accept(Snapshot());
        inbox.Accept(Snapshot(2) with { Tasks = [new("claude:sample", AgentProvider.Claude, "Example question",
            AgentTaskStatus.NeedsAttention, StateConfidence.Confirmed, Now)] });
        var local = StatusBarState.Empty with { Tasks = [Task()], WorkingCount = 1 };
        var visible = LiveTaskExchange.Attach(local, inbox.Current());
        Assert.Equal(1, visible.WorkingCount);
        Assert.Equal(1, visible.AttentionCount);
        Assert.Same(local.Tasks, visible.Tasks);
        var remote = Assert.Single(Assert.Single(visible.RemoteDevices).Tasks);
        Assert.True(remote.IsRemote);
        Assert.Null(remote.SessionReference);
        Assert.Null(TaskNavigation.CreateLink(remote));
        Assert.NotEqual(local.Tasks[0].Id, remote.Id);
    }

    [Fact]
    public void Rejects_duplicate_unknown_and_oversized_records()
    {
        static byte[] Bytes(LiveTaskSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot);
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(Bytes(Snapshot() with { Version = 2 })));
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(Bytes(Snapshot() with { Sequence = 0 })));
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(Bytes(Snapshot() with { DeviceId = "../sample" })));
        var tasks = Snapshot().Tasks;
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(Bytes(Snapshot() with { Tasks = [tasks[0], tasks[0]] })));
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(Bytes(Snapshot() with
        { Tasks = [tasks[0] with { Status = (AgentTaskStatus)999 }] })));
        Assert.Throws<InvalidDataException>(() => LiveTaskExchange.Read(new byte[LiveTaskExchange.MaximumBytes + 1]));
    }
}
