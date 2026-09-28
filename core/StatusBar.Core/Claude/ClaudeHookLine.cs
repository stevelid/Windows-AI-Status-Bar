using System.Text;
using System.Text.Json;

namespace StatusBar.Core.Claude;

/// <summary>Reduces one documented Claude Code hook payload to the app-owned minimal JSONL format.</summary>
public static class ClaudeHookLine
{
    static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "UserPromptSubmit",
        "Notification",
        "Stop",
        "SessionEnd",
    };

    /// <summary>Returns a content-free line, or <see langword="null"/> when required fields are invalid.</summary>
    /// <summary>Returns one redacted JSONL record, or <see langword="null"/> when required fields are invalid.</summary>
    public static string? FromHookJson(string json, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "hook_event_name", out var eventName) ||
                !AllowedEvents.Contains(eventName) ||
                !TryGetString(root, "session_id", out var sessionId) ||
                !IsSafeIdentifier(sessionId, 128))
            {
                return null;
            }

            string? notificationType = null;
            if (eventName == "Notification")
            {
                if (!TryGetString(root, "notification_type", out var value) || !IsSafeIdentifier(value, 64))
                    return null;
                notificationType = value;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("ts", (timeProvider ?? TimeProvider.System).GetUtcNow());
                writer.WriteString("event", eventName);
                writer.WriteString("session_id", sessionId);
                if (notificationType is not null)
                    writer.WriteString("notification_type", notificationType);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    static bool IsSafeIdentifier(string value, int maxLength) =>
        value.Length <= maxLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
