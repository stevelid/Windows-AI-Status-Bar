using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Codex;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Codex;

public sealed class CodexTaskProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");
    static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Codex_paths_use_settings_then_environment_then_profile_defaults()
    {
        var settings = CodexPaths.Resolve("settings-home", "environment-home", "profile-home");
        var environment = CodexPaths.Resolve(" ", "environment-home", "profile-home");
        var profile = CodexPaths.Resolve(null, null, "profile-home");

        Assert.Equal("settings-home", settings.Home);
        Assert.Equal(Path.Combine("settings-home", "sessions"), settings.SessionsDirectory);
        Assert.Equal(Path.Combine("settings-home", "session_index.jsonl"), settings.SessionIndexPath);
        Assert.Equal("environment-home", environment.Home);
        Assert.Equal(Path.Combine("profile-home", ".codex"), profile.Home);
    }

    [Fact]
    public async Task Discovers_two_sessions_from_current_and_previous_local_dates()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        WriteSession(SessionPath(paths, time, "today.jsonl"), "thread-today", time.GetUtcNow());
        var yesterday = time.GetLocalNow().Date.AddDays(-1);
        WriteSession(SessionPath(paths, time, "yesterday.jsonl", yesterday), "thread-yesterday", time.GetUtcNow());

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();

        Assert.Equal(2, provider.TrackedFiles);
        Assert.Equal(2, provider.Current.Tasks.Count);
        Assert.All(provider.Current.Tasks, task => Assert.Equal(AgentTaskStatus.Working, task.Status));
        Assert.Equal(ProviderHealthState.Ok, provider.Current.Health.State);

        var diagnostics = provider.Diagnostics;
        Assert.Equal(2, diagnostics.SessionsTracked);
        Assert.Equal(0, diagnostics.FilesWatched);
        Assert.Equal(TimeSpan.Zero, diagnostics.LastEventAge);
        Assert.Equal(0, diagnostics.ParseErrors);
        Assert.Equal(0, diagnostics.FormatDriftCount);
        Assert.Empty(diagnostics.FormatDriftBySignature);
        Assert.Equal(0, diagnostics.WatcherOverflowCount);

        time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(TimeSpan.FromSeconds(8), provider.Diagnostics.LastEventAge);
    }

    [Fact]
    public async Task Appends_continue_at_the_tail_offset_and_update_the_task()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        var sessionPath = SessionPath(paths, time, "large-rollout.jsonl");
        WriteSession(sessionPath, "thread-large", Now, paddingBytes: 2 * 1024 * 1024 + 128);

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();

        var initialPosition = Assert.Single(provider.ReaderPositions);
        Assert.True(initialPosition.Offset > 0);
        Assert.Equal(initialPosition.Offset, initialPosition.LastReadStartOffset);
        Assert.Equal(AgentTaskStatus.Working, Assert.Single(provider.Current.Tasks).Status);

        AppendRecord(sessionPath, EventRecord("task_complete", Now.AddSeconds(1)));
        await provider.ReconcileAsync();

        var updatedPosition = Assert.Single(provider.ReaderPositions);
        Assert.Equal(initialPosition.Offset, updatedPosition.LastReadStartOffset);
        Assert.Equal(AgentTaskStatus.Complete, Assert.Single(provider.Current.Tasks).Status);

        await using var restarted = new CodexTaskProvider(paths, time, watchFiles: false);
        await restarted.ReconcileAsync();
        Assert.Equal(AgentTaskStatus.Complete, Assert.Single(restarted.Current.Tasks).Status);
        Assert.True(Assert.Single(restarted.ReaderPositions).LastReadStartOffset > 0);
    }

    [Fact]
    public async Task Reconciliation_finds_new_sessions_when_file_watching_is_disabled()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();
        Assert.Empty(provider.Current.Tasks);

        WriteSession(SessionPath(paths, time, "new.jsonl"), "thread-new", time.GetUtcNow());
        await provider.ReconcileAsync();

        Assert.Single(provider.Current.Tasks);
        Assert.Equal("codex:thread-new", Assert.Single(provider.Current.Tasks).Id);
    }

    [Fact]
    public async Task Missing_sessions_folder_is_unavailable_until_it_is_created()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);

        await provider.ReconcileAsync();
        Assert.Equal(ProviderHealthState.Unavailable, provider.Current.Health.State);
        Assert.Equal("SessionsDirectoryMissing", provider.Current.Health.Code);

        WriteSession(SessionPath(paths, time, "created.jsonl"), "thread-created", time.GetUtcNow());
        await provider.ReconcileAsync();

        Assert.Equal(ProviderHealthState.Ok, provider.Current.Health.State);
        Assert.Single(provider.Current.Tasks);
    }

    [Fact]
    public void Session_reader_retries_discovery_after_sessions_directory_is_recreated()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        WriteSession(SessionPath(paths, time, "before-removal.jsonl"), "thread-before", Now);
        using var reader = new CodexSessionReader(paths, time, TaskTimings.Default);

        Assert.Single(reader.Reconcile());
        Directory.Delete(paths.SessionsDirectory, recursive: true);
        Assert.Empty(reader.Reconcile(forceDiscovery: false));

        WriteSession(SessionPath(paths, time, "after-recreation.jsonl"), "thread-after", Now);
        var tasks = reader.Reconcile(forceDiscovery: false);

        Assert.Equal("codex:thread-after", Assert.Single(tasks).Id);
    }

    [Fact]
    public async Task Compressed_jsonl_files_are_ignored()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        var compressedPath = SessionPath(paths, time, "compressed.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(compressedPath)!);
        File.WriteAllText(compressedPath, SessionMetaRecord("thread-compressed", "vscode", null, Now));

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();

        Assert.Equal(0, provider.TrackedFiles);
        Assert.Empty(provider.Current.Tasks);
    }

    [Fact]
    public async Task Subagent_attention_is_merged_into_parent_and_sibling_work_does_not_clear_it()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        WriteSession(SessionPath(paths, time, "parent.jsonl"), "thread-parent", Now.AddSeconds(-5));
        WriteSubagent(SessionPath(paths, time, "a-attention.jsonl"), "thread-attention", "thread-parent", Now.AddSeconds(-5), requestInput: true);
        WriteSubagent(SessionPath(paths, time, "z-working.jsonl"), "thread-working", "thread-parent", Now.AddSeconds(-5), requestInput: false);

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();

        var task = Assert.Single(provider.Current.Tasks);
        Assert.Equal("codex:thread-parent", task.Id);
        Assert.Equal(AgentTaskStatus.NeedsAttention, task.Status);
        Assert.Equal("Waiting for your input", task.AttentionReason);
    }

    [Fact]
    public async Task Guardian_review_sessions_are_hidden_and_spawned_subagents_fold_into_their_parent()
    {
        using var temp = new TempRoot();
        var time = new FakeTimeProvider(Now);
        var paths = CodexPaths.Resolve(overrideHome: temp.Path);
        WriteSession(SessionPath(paths, time, "parent.jsonl"), "thread-parent", Now.AddSeconds(-5));
        WriteRealShapeSession(SessionPath(paths, time, "guardian.jsonl"),
            "{\"subagent\":{\"other\":\"guardian\"}}", "guardian_review", Now.AddSeconds(-3));
        WriteRealShapeSession(SessionPath(paths, time, "spawned.jsonl"),
            "{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"thread-parent\"}}}", "subagent", Now.AddSeconds(-2));

        await using var provider = new CodexTaskProvider(paths, time, watchFiles: false);
        await provider.ReconcileAsync();

        var task = Assert.Single(provider.Current.Tasks);
        Assert.Equal("codex:thread-parent", task.Id);
        Assert.Equal(AgentTaskStatus.Working, task.Status);
    }

    static void WriteRealShapeSession(string path, string sourceJson, string threadSource, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var id = "thread-" + Path.GetFileNameWithoutExtension(path);
        using var writer = new StreamWriter(path, append: false, Utf8WithoutBom);
        writer.WriteLine($"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"source\":{sourceJson},\"thread_source\":\"{threadSource}\"}}}}");
        writer.WriteLine(EventRecord("task_started", timestamp));
    }

    static void WriteSession(string path, string threadId, DateTimeOffset timestamp, int paddingBytes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, Utf8WithoutBom);
        writer.WriteLine(SessionMetaRecord(threadId, "vscode", null, timestamp));
        if (paddingBytes > 0)
        {
            writer.Write("{\"type\":\"padding\",\"value\":\"");
            writer.Write(new string('x', paddingBytes));
            writer.WriteLine("\"}");
        }
        writer.WriteLine(EventRecord("task_started", timestamp));
    }

    static void WriteSubagent(string path, string threadId, string parentId, DateTimeOffset timestamp, bool requestInput)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, Utf8WithoutBom);
        writer.WriteLine(SessionMetaRecord(threadId, "sub-agent", parentId, timestamp));
        writer.WriteLine(EventRecord("task_started", timestamp));
        if (requestInput)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                timestamp,
                type = "response_item",
                payload = new { type = "function_call", call_id = "call-placeholder", name = "request_user_input" },
            }));
        }
    }

    static string SessionMetaRecord(string threadId, string source, string? parentId, DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "session_meta",
            payload = new { id = threadId, source, parent_thread_id = parentId },
        });

    static string EventRecord(string eventType, DateTimeOffset timestamp) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type = eventType },
    });

    static void AppendRecord(string path, string record)
    {
        using var writer = new StreamWriter(path, append: true, Utf8WithoutBom);
        writer.WriteLine(record);
    }

    static string SessionPath(CodexPaths paths, FakeTimeProvider time, string fileName, DateTime? localDate = null)
    {
        var date = localDate ?? time.GetLocalNow().DateTime;
        return Path.Combine(
            paths.SessionsDirectory,
            date.Year.ToString("D4"),
            date.Month.ToString("D2"),
            date.Day.ToString("D2"),
            fileName);
    }

    sealed class TempRoot : IDisposable
    {
        internal TempRoot() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "statusbar-codex-" + Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
