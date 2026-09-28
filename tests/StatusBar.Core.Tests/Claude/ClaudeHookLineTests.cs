using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Claude;

namespace StatusBar.Core.Tests.Claude;

public class ClaudeHookLineTests
{
    [Fact]
    public void Notification_line_contains_only_the_allowed_content_free_fields()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
        const string input = """
            {
              "hook_event_name":"Notification",
              "session_id":"synthetic-session",
              "notification_type":"permission_prompt",
              "prompt":"SYNTHETIC PROMPT TEXT",
              "message":"SYNTHETIC MESSAGE TEXT",
              "tool_input":{"path":"SYNTHETIC TOOL PATH"},
              "transcript_path":"SYNTHETIC TRANSCRIPT PATH",
              "cwd":"SYNTHETIC WORKING PATH"
            }
            """;

        var line = ClaudeHookLine.FromHookJson(input, time);

        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal(
            new[] { "ts", "event", "session_id", "notification_type" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(time.GetUtcNow(), root.GetProperty("ts").GetDateTimeOffset());
        Assert.Equal("Notification", root.GetProperty("event").GetString());
        Assert.Equal("synthetic-session", root.GetProperty("session_id").GetString());
        Assert.Equal("permission_prompt", root.GetProperty("notification_type").GetString());
        Assert.DoesNotContain("SYNTHETIC", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_notification_events_drop_notification_and_all_other_input_fields()
    {
        const string input = """
            {"hook_event_name":"UserPromptSubmit","session_id":"synthetic-session","notification_type":"ignored","prompt":"SYNTHETIC PROMPT"}
            """;

        var line = ClaudeHookLine.FromHookJson(input);

        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        Assert.Equal(new[] { "ts", "event", "session_id" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("SYNTHETIC", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"hook_event_name\":\"Unknown\",\"session_id\":\"synthetic-session\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\"}")]
    [InlineData("{\"hook_event_name\":\"Notification\",\"session_id\":\"synthetic-session\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\",\"session_id\":\"path with spaces\"}")]
    public void Invalid_or_unrecognized_hook_input_returns_null(string input)
    {
        Assert.Null(ClaudeHookLine.FromHookJson(input));
    }
}
