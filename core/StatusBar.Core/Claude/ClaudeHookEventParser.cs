using System.Globalization;
using System.Text.Json;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;

namespace StatusBar.Core.Claude;

/// <summary>One minimal event previously written by the app's Claude Code hook sink.</summary>
internal sealed record ClaudeHookEvent(
    DateTimeOffset Timestamp,
    string Event,
    string SessionId,
    string? NotificationType);

/// <summary>Reads app-owned hook records incrementally and skips malformed lines.</summary>
internal sealed class ClaudeHookEventParser(IncrementalJsonlReader reader, FormatDriftCounter drift)
{
    readonly IncrementalJsonlReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    readonly FormatDriftCounter _drift = drift ?? throw new ArgumentNullException(nameof(drift));

    internal IReadOnlyList<ClaudeHookEvent> ReadNewEvents()
    {
        var result = _reader.ReadNewLines();
        var events = new List<ClaudeHookEvent>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (TryParse(line, out var hookEvent))
                events.Add(hookEvent);
            else
                _drift.RecordMalformed();
        }
        return events;
    }

    static bool TryParse(string line, out ClaudeHookEvent hookEvent)
    {
        hookEvent = default!;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "event", out var eventName) ||
                !IsKnownEvent(eventName) ||
                !TryGetString(root, "session_id", out var sessionId) ||
                !IsSafeIdentifier(sessionId, 128) ||
                !TryGetString(root, "ts", out var timestampText) ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            {
                return false;
            }

            string? notificationType = null;
            if (eventName == "Notification")
            {
                if (!TryGetString(root, "notification_type", out var value)) return false;
                notificationType = value;
            }

            hookEvent = new ClaudeHookEvent(timestamp, eventName, sessionId, notificationType);
            return true;
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

    static bool IsKnownEvent(string eventName) =>
        eventName is "UserPromptSubmit" or "Notification" or "Stop" or "SessionEnd";

    static bool IsSafeIdentifier(string value, int maxLength) =>
        value.Length <= maxLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
