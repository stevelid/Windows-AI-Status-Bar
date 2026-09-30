using StatusBar.Core.Claude;
using StatusBar.Core.Codex;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Judgment;

[CollectionDefinition("FinalMessageCapture", DisableParallelization = true)]
public sealed class FinalMessageCaptureCollection;

[Collection("FinalMessageCapture")]
public sealed class TurnEndCaptureTests : IDisposable
{
    const string Message = "Placeholder final message. Shall I continue with the sample?";
    readonly string _root = Path.Combine(Path.GetTempPath(), "statusbar-turnend-" + Guid.NewGuid().ToString("N"));

    public TurnEndCaptureTests() => FinalMessageCapture.Enabled = false;

    public void Dispose()
    {
        FinalMessageCapture.Enabled = false;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Codex_raises_the_finished_turn_only_when_capture_is_on_and_the_turn_ends_live()
    {
        FinalMessageCapture.Enabled = true;
        var paths = CodexPaths.Resolve(overrideHome: _root);
        var now = DateTime.Now;
        var file = Path.Combine(paths.SessionsDirectory, now.Year.ToString("D4"), now.Month.ToString("D2"), now.Day.ToString("D2"),
            "rollout-2026-01-01T00-00-00-0199a000-0000-7000-8000-00000000abcd.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // A turn that already finished before the app looked must not be judged.
        File.AppendAllText(file,
            "{\"timestamp\":\"2026-01-01T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"0199a000-0000-7000-8000-00000000abcd\",\"source\":\"vscode\"}}\n" +
            "{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n" +
            "{\"timestamp\":\"2026-01-01T12:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"Old placeholder.\"}}\n" +
            "{\"timestamp\":\"2026-01-01T12:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(paths, TimeProvider.System, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();
        Assert.Empty(seen);

        File.AppendAllText(file,
            "{\"timestamp\":\"2026-01-01T12:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"" + Message + "\"}}\n");
        await provider.ReconcileAsync();

        var info = Assert.Single(seen);
        Assert.Equal(AgentProvider.Codex, info.Provider);
        Assert.Equal("codex:0000abcd", info.TaskKey);
        Assert.Equal("question", info.Heuristic);
        Assert.Equal(Message, info.Text);

        // The same turn is not raised again.
        await provider.ReconcileAsync();
        Assert.Single(seen);
    }

    [Fact]
    public async Task Codex_keeps_no_text_and_raises_nothing_when_capture_is_off()
    {
        var paths = CodexPaths.Resolve(overrideHome: _root);
        var now = DateTime.Now;
        var file = Path.Combine(paths.SessionsDirectory, now.Year.ToString("D4"), now.Month.ToString("D2"), now.Day.ToString("D2"),
            "rollout-2026-01-01T00-00-00-0199a000-0000-7000-8000-00000000abcd.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file,
            "{\"timestamp\":\"2026-01-01T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"0199a000-0000-7000-8000-00000000abcd\",\"source\":\"vscode\"}}\n" +
            "{\"timestamp\":\"2026-01-01T12:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(paths, TimeProvider.System, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();

        File.AppendAllText(file,
            "{\"timestamp\":\"2026-01-01T12:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"" + Message + "\"}}\n");
        await provider.ReconcileAsync();

        Assert.Empty(seen);
    }

    [Fact]
    public async Task Claude_raises_the_finished_turn_with_the_final_text_tail()
    {
        FinalMessageCapture.Enabled = true;
        var paths = ClaudeCodePaths.Resolve(overrideHome: _root);
        var file = Path.Combine(paths.ProjectsDirectory, "project-folder", "0199a000-0000-7000-8000-00000000dcba.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file,
            "{\"type\":\"user\",\"sessionId\":\"0199a000-0000-7000-8000-00000000dcba\",\"timestamp\":\"2026-01-01T12:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Sample request\"}]}}\n");
        var seen = new List<TurnEndInfo>();
        await using var provider = new ClaudeCodeTaskProvider(paths, null, TimeProvider.System, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();
        Assert.Empty(seen);

        var longText = new string('x', 3000) + " " + Message;
        File.AppendAllText(file,
            "{\"type\":\"assistant\",\"sessionId\":\"0199a000-0000-7000-8000-00000000dcba\",\"timestamp\":\"2026-01-01T12:00:05Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + longText + "\"}],\"stop_reason\":\"end_turn\"}}\n");
        await provider.ReconcileAsync();

        var info = Assert.Single(seen);
        Assert.Equal("claude:0000dcba", info.TaskKey);
        Assert.Equal("question", info.Heuristic);
        Assert.Equal(FinalMessageCapture.MaximumCharacters, info.Text.Length);
        Assert.EndsWith(Message, info.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_is_off_by_default_and_ignores_blank_text()
    {
        Assert.False(FinalMessageCapture.Enabled);
        Assert.Null(FinalMessageCapture.Tail("anything"));

        FinalMessageCapture.Enabled = true;
        Assert.Null(FinalMessageCapture.Tail("   "));
        Assert.Null(FinalMessageCapture.Tail(null));
        Assert.Equal("kept", FinalMessageCapture.Tail("  kept  "));
    }
}
