using System.Text;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Claude;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;

namespace StatusBar.Core.Tests.Claude;

public class ClaudeHookEventParserTests
{
    [Fact]
    public void Reads_hook_lines_incrementally_and_skips_malformed_records()
    {
        using var file = new TempJsonlFile();
        var reader = new IncrementalJsonlReader(file.Path);
        var drift = new FormatDriftCounter();
        var parser = new ClaudeHookEventParser(reader, drift);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
        var notification = ClaudeHookLine.FromHookJson(
            "{\"hook_event_name\":\"Notification\",\"session_id\":\"synthetic-session\",\"notification_type\":\"permission_prompt\"}",
            time)!;
        file.Write(notification + "\n{bad line\n");

        var first = parser.ReadNewEvents();
        time.Advance(TimeSpan.FromSeconds(1));
        var stop = ClaudeHookLine.FromHookJson(
            "{\"hook_event_name\":\"Stop\",\"session_id\":\"synthetic-session\"}",
            time)!;
        file.Append(stop + "\n");
        var second = parser.ReadNewEvents();

        Assert.Single(first);
        Assert.Equal("Notification", first[0].Event);
        Assert.Equal("permission_prompt", first[0].NotificationType);
        Assert.Single(second);
        Assert.Equal("Stop", second[0].Event);
        Assert.Equal(1, drift.MalformedCount);
    }

    sealed class TempJsonlFile : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "statusbar-hooks-" + Guid.NewGuid().ToString("N"));

        internal TempJsonlFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "events.jsonl");
        }

        internal string Path { get; }
        internal void Write(string text) => File.WriteAllText(Path, text);

        internal void Append(string text)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }
}
