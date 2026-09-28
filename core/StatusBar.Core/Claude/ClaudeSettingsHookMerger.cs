using System.Text.Json;
using System.Text.Json.Nodes;

namespace StatusBar.Core.Claude;

/// <summary>Merges the app's command hooks into Claude Code settings without replacing other settings.</summary>
public static class ClaudeSettingsHookMerger
{
    static readonly string[] HookEvents = ["UserPromptSubmit", "Notification", "Stop", "SessionEnd"];
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Adds or updates this app's four hooks while preserving other hooks and top-level settings.</summary>
    public static string Add(string json, string command)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.Contains("--claude-hook", StringComparison.Ordinal))
            throw new ArgumentException("The command must include --claude-hook.", nameof(command));

        var root = ParseRoot(json);
        var changed = RemoveHookEntries(root, command);
        var hooks = GetOrCreateHooks(root);
        foreach (var eventName in HookEvents)
        {
            if (ContainsCommand(hooks, eventName, command)) continue;
            var groups = GetOrCreateGroups(hooks, eventName);
            groups.Add(new JsonObject
            {
                ["matcher"] = "",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                }),
            });
            changed = true;
        }

        return changed ? Serialize(root) : json;
    }

    /// <summary>Removes only command hook entries whose command contains <c>--claude-hook</c>.</summary>
    public static string Remove(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var root = ParseRoot(json);
        return RemoveHookEntries(root, keepCommand: null) ? Serialize(root) : json;
    }

    /// <summary>Reports whether the exact command is installed under a Claude hook event.</summary>
    public static bool IsInstalled(string json, string command)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return ContainsCommand(ParseRoot(json), command);
    }

    static JsonObject ParseRoot(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        return root ?? throw new JsonException("Claude settings must be a JSON object.");
    }

    static string Serialize(JsonObject root) => root.ToJsonString(PrettyJson);

    static JsonObject GetOrCreateHooks(JsonObject root)
    {
        if (!root.TryGetPropertyValue("hooks", out var value) || value is null)
        {
            var created = new JsonObject();
            root["hooks"] = created;
            return created;
        }

        return value as JsonObject ?? throw new JsonException("Claude hooks must be a JSON object.");
    }

    static JsonArray GetOrCreateGroups(JsonObject hooks, string eventName)
    {
        if (!hooks.TryGetPropertyValue(eventName, out var value) || value is null)
        {
            var created = new JsonArray();
            hooks[eventName] = created;
            return created;
        }

        return value as JsonArray ?? throw new JsonException("A Claude hook event must contain an array.");
    }

    static bool RemoveHookEntries(JsonObject root, string? keepCommand)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        var changed = false;
        foreach (var groups in hooks.Select(property => property.Value).OfType<JsonArray>())
        {
            for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (groups[groupIndex] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;
                var removedFromGroup = false;
                for (var entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    if (entries[entryIndex] is not JsonObject entry || !TryGetCommand(entry, out var command) ||
                        !command.Contains("--claude-hook", StringComparison.Ordinal) || command == keepCommand)
                    {
                        continue;
                    }

                    entries.RemoveAt(entryIndex);
                    changed = true;
                    removedFromGroup = true;
                }

                if (removedFromGroup && entries.Count == 0)
                    groups.RemoveAt(groupIndex);
            }
        }

        return changed;
    }

    static bool ContainsCommand(JsonObject root, string command)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        return HookEvents.All(eventName => ContainsCommand(hooks, eventName, command));
    }

    static bool ContainsCommand(JsonObject hooks, string eventName, string command)
    {
        if (hooks[eventName] is not JsonArray groups) return false;
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray entries) continue;
            if (entries.OfType<JsonObject>().Any(entry => TryGetCommand(entry, out var value) && value == command))
                return true;
        }

        return false;
    }

    static bool TryGetCommand(JsonObject entry, out string command)
    {
        command = string.Empty;
        if (entry["command"] is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null)
            return false;
        command = text;
        return true;
    }
}
