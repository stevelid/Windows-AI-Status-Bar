using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Claude;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Claude;

public sealed class ClaudeCodeTaskProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");
    static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task Discovers_two_recent_sessions()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = ClaudeCodePaths.Resolve(overrideHome: temp.Path);
        WriteTranscript(SessionPath(paths, "session-a"), "session-a", "project-a", Now,
            AssistantRecord("session-a", "project-a", Now));
        WriteTranscript(SessionPath(paths, "session-b"), "session-b", "project-b", Now.AddSeconds(-1),
            AssistantRecord("session-b", "project-b", Now.AddSeconds(-1)));

        await using var provider = NewProvider(paths, time);
        await provider.ReconcileAsync();

        Assert.Equal(2, provider.TrackedFiles);
        Assert.Equal(2, provider.Current.Tasks.Count);
        Assert.All(provider.Current.Tasks, task => Assert.Equal(AgentTaskStatus.Working, task.Status));
        Assert.Contains(provider.Current.Tasks, task => task.Title == "Claude · project-a");
        Assert.Contains(provider.Current.Tasks, task => task.Title == "Claude · project-b");
        Assert.Equal(ProviderHealthState.Ok, provider.Current.Health.State);
    }

    [Fact]
    public async Task Hook_permission_alert_is_cleared_by_a_later_transcript_record()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = ClaudeCodePaths.Resolve(overrideHome: temp.Path);
        var sessionPath = SessionPath(paths, "session-hook");
        var hookPath = Path.Combine(temp.Path, "claude-hooks.jsonl");
        WriteTranscript(sessionPath, "session-hook", "project-hook", Now.AddSeconds(-5),
            AssistantRecord("session-hook", "project-hook", Now.AddSeconds(-5)));
        WriteHook(hookPath, "session-hook", "Notification", "permission_prompt", Now);

        await using var provider = NewProvider(paths, time, hookPath);
        await provider.ReconcileAsync();

        var attention = Assert.Single(provider.Current.Tasks);
        Assert.Equal(AgentTaskStatus.NeedsAttention, attention.Status);
        Assert.Equal(StateConfidence.Confirmed, attention.Confidence);
        Assert.Equal("Permission requested", attention.AttentionReason);

        time.Advance(TimeSpan.FromSeconds(1));
        AppendRecord(sessionPath, AssistantRecord("session-hook", "project-hook", time.GetUtcNow()));
        SetWriteTime(sessionPath, time.GetUtcNow());
        await provider.ReconcileAsync();

        var resumed = Assert.Single(provider.Current.Tasks);
        Assert.Equal(AgentTaskStatus.Working, resumed.Status);
        Assert.Equal(StateConfidence.Confirmed, resumed.Confidence);
        Assert.Null(resumed.AttentionReason);
    }

    [Fact]
    public async Task Missing_hook_file_uses_transcript_state_only()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = ClaudeCodePaths.Resolve(overrideHome: temp.Path);
        var missingHookPath = Path.Combine(temp.Path, "claude-hooks.jsonl");
        WriteTranscript(SessionPath(paths, "session-transcript"), "session-transcript", "transcript-only", Now,
            AssistantRecord("session-transcript", "transcript-only", Now));

        await using var provider = NewProvider(paths, time, missingHookPath);
        await provider.ReconcileAsync();

        var task = Assert.Single(provider.Current.Tasks);
        Assert.Equal(AgentTaskStatus.Working, task.Status);
        Assert.Equal(StateConfidence.Confirmed, task.Confidence);
        Assert.Equal("Claude · transcript-only", task.Title);
    }

    [Fact]
    public async Task Sidechain_only_records_do_not_create_tasks()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = ClaudeCodePaths.Resolve(overrideHome: temp.Path);
        WriteTranscript(SessionPath(paths, "session-sidechain"), "session-sidechain", "subtask", Now,
            AssistantRecord("session-sidechain", "subtask", Now, isSidechain: true));

        await using var provider = NewProvider(paths, time);
        await provider.ReconcileAsync();

        Assert.Empty(provider.Current.Tasks);
    }

    [Fact]
    public async Task Restart_recovers_current_state_from_the_transcript_tail()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = ClaudeCodePaths.Resolve(overrideHome: temp.Path);
        var sessionPath = SessionPath(paths, "session-restart");
        WriteTranscript(sessionPath, "session-restart", "restart", Now,
            AssistantRecord("session-restart", "restart", Now));

        await using (var firstRun = NewProvider(paths, time))
        {
            await firstRun.ReconcileAsync();
            Assert.Equal(AgentTaskStatus.Working, Assert.Single(firstRun.Current.Tasks).Status);
        }

        await using var restarted = NewProvider(paths, time);
        await restarted.ReconcileAsync();

        Assert.Equal(AgentTaskStatus.Working, Assert.Single(restarted.Current.Tasks).Status);
        Assert.True(Assert.Single(restarted.ReaderPositions).LastReadStartOffset > 0);
    }

    static ClaudeCodeTaskProvider NewProvider(ClaudeCodePaths paths, FakeTimeProvider time, string? hookPath = null) =>
        new(paths, hookPath, time, watchFiles: false);

    static string SessionPath(ClaudeCodePaths paths, string sessionId) =>
        Path.Combine(paths.ProjectsDirectory, "project-folder", sessionId + ".jsonl");

    static void WriteTranscript(string path, string sessionId, string cwd, DateTimeOffset writeTime, string record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(path, append: false, Utf8WithoutBom))
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                type = "summary",
                sessionId,
                cwd,
                timestamp = writeTime,
            }));
            writer.WriteLine(record);
        }
        SetWriteTime(path, writeTime);
    }

    static string AssistantRecord(string sessionId, string cwd, DateTimeOffset timestamp, bool isSidechain = false) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId,
            cwd,
            timestamp,
            isSidechain,
            message = new
            {
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
            },
        });

    static void WriteHook(string path, string sessionId, string eventName, string? notificationType, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, Utf8WithoutBom);
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            ts = timestamp,
            @event = eventName,
            session_id = sessionId,
            notification_type = notificationType,
        }));
    }

    static void AppendRecord(string path, string record)
    {
        using var writer = new StreamWriter(path, append: true, Utf8WithoutBom);
        writer.WriteLine(record);
    }

    static void SetWriteTime(string path, DateTimeOffset timestamp) =>
        File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);

    sealed class TempRoot : IDisposable
    {
        internal TempRoot() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "statusbar-claude-" + Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
