using System.Text.Json;
using StatusBar.Core.Common;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;

namespace StatusBar.Core.Codex;

/// <summary>Resolves Codex display titles from the session index and sanitized rollout metadata.</summary>
internal sealed class CodexTitleResolver(IncrementalJsonlReader indexReader, FormatDriftCounter drift)
{
    readonly IncrementalJsonlReader _indexReader = indexReader ?? throw new ArgumentNullException(nameof(indexReader));
    readonly FormatDriftCounter _drift = drift ?? throw new ArgumentNullException(nameof(drift));
    readonly Dictionary<string, string?> _indexedTitles = new(StringComparer.Ordinal);

    internal bool Refresh()
    {
        var result = _indexReader.ReadNewLines();
        if (result.Reset) _indexedTitles.Clear();

        var changed = result.Reset;
        foreach (var line in result.Lines)
        {
            if (!TryApplyIndexEntry(line)) continue;
            changed = true;
        }
        return changed;
    }

    internal string Resolve(CodexSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.IsNullOrWhiteSpace(state.ThreadId) &&
            _indexedTitles.TryGetValue(state.ThreadId, out var indexedTitle) &&
            indexedTitle is not null)
        {
            return indexedTitle;
        }

        if (state.TitleCandidate is { Length: > 0 } candidate) return candidate;
        if (IsSubAgent(state.Source)) return "Codex sub-task";
        if (state.CwdLeaf is { Length: > 0 } leaf)
            return TextSanitizer.SanitizeTitleCandidate("Codex · " + leaf) ?? "Codex task";
        return "Codex task";
    }

    bool TryApplyIndexEntry(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            _drift.RecordMalformed();
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "id", out var id) || string.IsNullOrWhiteSpace(id) ||
                !TryGetString(root, "thread_name", out var threadName))
            {
                _drift.RecordMalformed();
                return false;
            }

            // ⚠️ A-X5 The append-only session index's latest thread_name is the preferred user title.
            _indexedTitles[id] = TextSanitizer.SanitizeTitleCandidate(threadName);
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
        return true;
    }

    static bool IsSubAgent(string? source) =>
        string.Equals(source, "sub-agent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "sub_agent", StringComparison.OrdinalIgnoreCase);
}
