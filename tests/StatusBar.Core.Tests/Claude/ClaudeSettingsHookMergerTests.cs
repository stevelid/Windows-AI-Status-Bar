using System.Text.Json;
using StatusBar.Core.Claude;

namespace StatusBar.Core.Tests.Claude;

public class ClaudeSettingsHookMergerTests
{
    const string Command = "AIStatusBar.exe --claude-hook";
    static readonly string[] Events = ["UserPromptSubmit", "Notification", "Stop", "SessionEnd"];

    [Fact]
    public void Add_to_empty_settings_creates_the_four_documented_events()
    {
        var settings = ClaudeSettingsHookMerger.Add("{}", Command);
        using var document = JsonDocument.Parse(settings);
        var hooks = document.RootElement.GetProperty("hooks");

        Assert.Equal(Events, hooks.EnumerateObject().Select(property => property.Name));
        foreach (var eventName in Events)
        {
            var command = hooks.GetProperty(eventName)[0].GetProperty("hooks")[0].GetProperty("command").GetString();
            Assert.Equal(Command, command);
        }
        Assert.True(ClaudeSettingsHookMerger.IsInstalled(settings, Command));
    }

    [Fact]
    public void Existing_hooks_and_unknown_top_level_settings_are_preserved()
    {
        const string original = """
            {
              "model": "synthetic-model",
              "customSetting": { "value": 42 },
              "hooks": {
                "Notification": [
                  { "matcher": "permission_prompt", "hooks": [{ "type": "command", "command": "other-command" }] }
                ]
              }
            }
            """;

        var updated = ClaudeSettingsHookMerger.Add(original, Command);
        using var document = JsonDocument.Parse(updated);
        var root = document.RootElement;

        Assert.Equal("synthetic-model", root.GetProperty("model").GetString());
        Assert.Equal(42, root.GetProperty("customSetting").GetProperty("value").GetInt32());
        var notificationGroups = root.GetProperty("hooks").GetProperty("Notification");
        Assert.Equal(2, notificationGroups.GetArrayLength());
        Assert.Equal("permission_prompt", notificationGroups[0].GetProperty("matcher").GetString());
        Assert.Equal("other-command", notificationGroups[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void Add_is_idempotent_and_repairs_a_partial_install()
    {
        var once = ClaudeSettingsHookMerger.Add("{}", Command);
        var twice = ClaudeSettingsHookMerger.Add(once, Command);

        Assert.Equal(once, twice);
        var partial = "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"command\":\"AIStatusBar.exe --claude-hook\"}]}]}}";
        var repaired = ClaudeSettingsHookMerger.Add(partial, Command);
        Assert.True(ClaudeSettingsHookMerger.IsInstalled(repaired, Command));
    }

    [Fact]
    public void Add_replaces_old_hook_commands_when_the_executable_path_changes()
    {
        const string oldCommand = "OldAIStatusBar.exe --claude-hook";
        var original = ClaudeSettingsHookMerger.Add("{}", oldCommand);

        var updated = ClaudeSettingsHookMerger.Add(original, Command);

        Assert.False(ClaudeSettingsHookMerger.IsInstalled(updated, oldCommand));
        Assert.True(ClaudeSettingsHookMerger.IsInstalled(updated, Command));
        Assert.DoesNotContain(oldCommand, updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_deletes_only_hook_entries_with_the_managed_flag()
    {
        const string original = """
            {
              "hooks": {
                "Notification": [
                  { "matcher": "", "hooks": [
                    { "type": "command", "command": "AIStatusBar.exe --claude-hook" },
                    { "type": "command", "command": "keep-this-command" }
                  ] }
                ],
                "PreToolUse": [
                  { "matcher": "Read", "hooks": [{ "type": "command", "command": "another-command" }] }
                ]
              },
              "unknown": "preserved"
            }
            """;

        var updated = ClaudeSettingsHookMerger.Remove(original);
        using var document = JsonDocument.Parse(updated);
        var root = document.RootElement;

        Assert.Equal("preserved", root.GetProperty("unknown").GetString());
        var entries = root.GetProperty("hooks").GetProperty("Notification")[0].GetProperty("hooks");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("keep-this-command", entries[0].GetProperty("command").GetString());
        Assert.Equal("another-command", root.GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.False(ClaudeSettingsHookMerger.IsInstalled(updated, Command));
    }

    [Fact]
    public void Malformed_json_is_rejected_before_any_transformation()
    {
        Assert.ThrowsAny<JsonException>(() => ClaudeSettingsHookMerger.Add("{", Command));
        Assert.ThrowsAny<JsonException>(() => ClaudeSettingsHookMerger.Remove("{"));
    }

    [Fact]
    public void Non_object_hooks_are_refused_instead_of_overwritten()
    {
        Assert.Throws<JsonException>(() => ClaudeSettingsHookMerger.Add("{\"hooks\":[]}", Command));
    }
}
