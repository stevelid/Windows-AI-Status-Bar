using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class DismissalStoreTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    [Fact]
    public void Dismissals_are_scoped_to_evidence_hashed_and_expire_after_24_hours()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        var store = new DismissalStore(time, statePath);

        store.Dismiss("claude:synthetic-session", "question:one");

        Assert.True(store.Contains("claude:synthetic-session", "question:one"));
        Assert.False(store.Contains("claude:synthetic-session", "question:two"));
        Assert.False(store.Contains("claude:other-session", "question:one"));
        var saved = File.ReadAllText(statePath);
        Assert.DoesNotContain("claude:synthetic-session", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("question:one", saved, StringComparison.Ordinal);

        var restarted = new DismissalStore(time, statePath);
        Assert.True(restarted.Contains("claude:synthetic-session", "question:one"));

        time.Advance(TimeSpan.FromHours(24));
        Assert.False(restarted.Contains("claude:synthetic-session", "question:one"));
    }

    [Fact]
    public void Updating_dismissals_preserves_other_state_sections()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        File.WriteAllText(statePath, "{\"notifications\":{\"seeded\":true}}");
        var store = new DismissalStore(time, statePath);

        store.Dismiss("codex:synthetic-thread", "pending-call:synthetic-call");

        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.True(document.RootElement.GetProperty("notifications").GetProperty("seeded").GetBoolean());
        Assert.Single(document.RootElement.GetProperty("dismissals").EnumerateArray());
    }

    [Fact]
    public void Malformed_state_is_left_untouched()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var statePath = Path.Combine(temp.Path, "state.json");
        const string malformed = "{ not-json";
        File.WriteAllText(statePath, malformed);
        var store = new DismissalStore(time, statePath);

        store.Dismiss("claude:synthetic-session", "question:one");

        Assert.Equal(malformed, File.ReadAllText(statePath));
        Assert.True(store.Contains("claude:synthetic-session", "question:one"));
    }

    sealed class TempRoot : IDisposable
    {
        internal TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "statusbar-dismissal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
