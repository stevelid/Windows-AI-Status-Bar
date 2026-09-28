using System.Text;
using StatusBar.Core.Codex;
using StatusBar.Core.Common;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;

namespace StatusBar.Core.Tests.Codex;

public class CodexTitleResolverTests
{
    [Fact]
    public void Latest_incremental_index_title_wins_and_is_sanitized()
    {
        using var file = new TempIndexFile();
        file.Write("{\"id\":\"thread-1\",\"thread_name\":\"**First title**\"}\n");
        var drift = new FormatDriftCounter();
        var resolver = new CodexTitleResolver(new IncrementalJsonlReader(file.Path), drift);
        var state = new CodexSessionState { ThreadId = "thread-1", TitleCandidate = "prompt fallback" };
        resolver.Refresh();
        Assert.Equal("First title", resolver.Resolve(state));

        file.Append("{\"id\":\"thread-1\",\"thread_name\":\"## 🧪 **Latest title**\"}\n");
        resolver.Refresh();

        Assert.Equal("🧪 Latest title", resolver.Resolve(state));
        Assert.Equal(0, drift.MalformedCount);
    }

    [Fact]
    public void Replacement_index_clears_old_titles_before_reading_new_entries()
    {
        using var file = new TempIndexFile();
        file.Write("{\"id\":\"thread-1\",\"thread_name\":\"Old title\"}\n");
        var resolver = new CodexTitleResolver(new IncrementalJsonlReader(file.Path), new FormatDriftCounter());
        var state = new CodexSessionState { ThreadId = "thread-1", CwdLeaf = "project" };
        resolver.Refresh();
        Assert.Equal("Old title", resolver.Resolve(state));

        file.Write("{\"id\":\"t2\",\"thread_name\":\"x\"}\n");
        resolver.Refresh();

        Assert.Equal("Codex · project", resolver.Resolve(state));
    }

    [Fact]
    public void Injected_context_is_skipped_and_markdown_emoji_are_safe()
    {
        var state = new CodexSessionState { ThreadId = "thread-1", CwdLeaf = "project" };
        var drift = new FormatDriftCounter();
        CodexRolloutParser.Apply(state,
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>\"}]}}",
            DateTimeOffset.UnixEpoch,
            drift);

        Assert.Null(state.TitleCandidate);
        Assert.Equal("Codex · project", new CodexTitleResolver(
            new IncrementalJsonlReader(MissingIndexPath()), drift).Resolve(state));
        Assert.Equal("🧪 Fix parser", TextSanitizer.SanitizeTitleCandidate("## 🧪 **Fix parser**"));
    }

    [Fact]
    public void Title_fallback_order_is_candidate_then_project_leaf_then_generic()
    {
        var resolver = new CodexTitleResolver(new IncrementalJsonlReader(MissingIndexPath()), new FormatDriftCounter());

        Assert.Equal("First request", resolver.Resolve(new CodexSessionState { TitleCandidate = "First request", CwdLeaf = "project" }));
        Assert.Equal("Codex · project", resolver.Resolve(new CodexSessionState { CwdLeaf = "project" }));
        Assert.Equal("Codex sub-task", resolver.Resolve(new CodexSessionState { Source = "sub-agent" }));
        Assert.Equal("Codex task", resolver.Resolve(new CodexSessionState()));
    }

    [Fact]
    public void Long_title_is_truncated_at_a_word_boundary_without_splitting_emoji()
    {
        var title = TextSanitizer.SanitizeTitleCandidate("A 🧪 long title with several useful words beyond the title limit")!;

        Assert.Equal("A 🧪 long title with several useful words…", title);
    }

    [Fact]
    public void Malformed_index_records_are_counted_without_retaining_their_content()
    {
        using var file = new TempIndexFile();
        file.Write("{\"id\":\"thread-1\",\"thread_name\":\"Safe title\"}\n{\"id\":\n");
        var drift = new FormatDriftCounter();
        var resolver = new CodexTitleResolver(new IncrementalJsonlReader(file.Path), drift);

        resolver.Refresh();

        Assert.Equal(1, drift.MalformedCount);
        Assert.Equal("Safe title", resolver.Resolve(new CodexSessionState { ThreadId = "thread-1" }));
    }

    sealed class TempIndexFile : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "statusbar-index-" + Guid.NewGuid().ToString("N"));

        internal TempIndexFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "session_index.jsonl");
        }

        internal string Path { get; }

        internal void Write(string content) => File.WriteAllText(Path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        internal void Append(string content)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }

    static string MissingIndexPath() => Path.Combine(Path.GetTempPath(), "missing-index-" + Guid.NewGuid().ToString("N"));
}
