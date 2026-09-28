using System.Text.Json;
using StatusBar.Core.Common;
using StatusBar.Core.Diagnostics;

namespace StatusBar.Core.Claude;

/// <summary>Applies one Claude Code transcript record to its in-memory session state.</summary>
internal static class ClaudeCodeTranscriptParser
{
    const string InterruptedPrefix = "[Request interrupted by user";

    internal static void Apply(
        ClaudeCodeSessionState state,
        string line,
        DateTimeOffset fallbackTime,
        FormatDriftCounter drift)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(drift);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            drift.RecordMalformed();
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetString(root, "type", out var recordType))
            {
                drift.RecordMalformed();
                return;
            }

            state.LastActivity = ReadTimestamp(root, fallbackTime);
            state.HasActivity = true;
            ApplySessionMetadata(state, root);

            if (IsTrue(GetProperty(root, "isSidechain")))
            {
                // ⚠️ A-K2 Sidechain records belong to the parent task and do not change turn state.
                state.HasSidechainActivity = true;
                return;
            }

            switch (recordType)
            {
                case "user":
                    ApplyUserRecord(state, root);
                    return;
                case "assistant":
                    ApplyAssistantRecord(state, root);
                    return;
                case "custom-title":
                    ApplyTitleRecord(root, "customTitle", value => state.CustomTitleCandidate = value);
                    return;
                case "ai-title":
                    ApplyTitleRecord(root, "aiTitle", value => state.AiTitleCandidate = value);
                    return;
                // Transcript metadata and future record types are activity only. In particular,
                // last-prompt content can contain the full user prompt and is deliberately ignored.
                default:
                    return;
            }
        }
    }

    static void ApplySessionMetadata(ClaudeCodeSessionState state, JsonElement root)
    {
        // ⚠️ A-K2 These transcript metadata fields are provisional beyond the observed record signatures.
        var sessionId = ReadString(root, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId)) state.SessionId = sessionId;

        var cwd = ReadString(root, "cwd");
        if (string.IsNullOrWhiteSpace(cwd)) return;

        var trimmed = cwd.TrimEnd('/', '\\');
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var leaf = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        state.CwdLeaf = TextSanitizer.SanitizeTitleCandidate(leaf);
    }

    static void ApplyUserRecord(ClaudeCodeSessionState state, JsonElement root)
    {
        // ⚠️ A-K2 User records and text/tool_result content blocks are only structurally inferred so far.
        var message = GetObject(root, "message");
        if (IsTrue(GetProperty(root, "isMeta")) || IsTrue(GetProperty(message, "isMeta"))) return;

        var content = GetProperty(message, "content");
        var hasToolResult = false;
        string? firstText = null;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var blockType = ReadString(block, "type");
                if (string.Equals(blockType, "tool_result", StringComparison.Ordinal))
                {
                    hasToolResult = true;
                    var toolUseId = ReadString(block, "tool_use_id");
                    if (!string.IsNullOrWhiteSpace(toolUseId)) state.PendingTools.Remove(toolUseId);
                }
                else if (firstText is null && string.Equals(blockType, "text", StringComparison.Ordinal))
                {
                    firstText = ReadString(block, "text");
                }
            }
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            firstText = content.GetString();
        }

        if (hasToolResult || string.IsNullOrWhiteSpace(firstText)) return;

        var text = firstText.TrimStart();
        if (text.StartsWith(InterruptedPrefix, StringComparison.Ordinal))
        {
            // ⚠️ A-K3 This interruption marker is unverified and remains a provisional signal.
            state.HasSeenTurnEvent = true;
            state.HasNonSidechainActivity = true;
            state.Turn = ClaudeCodeTurnStatus.Aborted;
            state.EndedWithQuestion = false;
            state.PendingTools.Clear();
            return;
        }

        if (IsSlashCommandWrapper(text)) return;

        state.HasSeenTurnEvent = true;
        state.HasNonSidechainActivity = true;
        state.Turn = ClaudeCodeTurnStatus.Running;
        state.EndedWithQuestion = false;
        state.PendingTools.Clear();
        if (state.FirstPromptTitleCandidate is null)
            state.FirstPromptTitleCandidate = TextSanitizer.SanitizeTitleCandidate(firstText);
    }

    // ⚠️ A-K5 A structured question or plan approval is an assistant tool_use named AskUserQuestion or
    // ExitPlanMode that stays unanswered (no matching tool_result) until Steve responds. Confirmed for the
    // same agent runtime in Cowork (A-C5); not yet observed in a Claude Code transcript.
    static ClaudePendingToolKind ToolKind(string? name) => name switch
    {
        "AskUserQuestion" => ClaudePendingToolKind.Question,
        "ExitPlanMode" => ClaudePendingToolKind.PlanApproval,
        _ => ClaudePendingToolKind.Other,
    };

    static void ApplyAssistantRecord(ClaudeCodeSessionState state, JsonElement root)
    {
        // ⚠️ A-K2 Assistant content blocks and tool_use IDs are not confirmed by the redacted recon report.
        var message = GetObject(root, "message");
        var content = GetProperty(message, "content");
        string? lastText = null;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var blockType = ReadString(block, "type");
                if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
                {
                    // Tool inputs can contain prompts, paths and credentials; only retain the opaque id
                    // and the kind derived from the tool's fixed name.
                    var toolUseId = ReadString(block, "id");
                    if (!string.IsNullOrWhiteSpace(toolUseId))
                    {
                        state.PendingTools[toolUseId] = new ClaudePendingTool(
                            toolUseId,
                            state.LastActivity,
                            ToolKind(ReadString(block, "name")));
                    }
                }
                else if (string.Equals(blockType, "text", StringComparison.Ordinal))
                {
                    lastText = ReadString(block, "text");
                }
            }
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            lastText = content.GetString();
        }

        state.HasSeenTurnEvent = true;
        state.HasNonSidechainActivity = true;
        state.Turn = ClaudeCodeTurnStatus.Running;
        state.EndedWithQuestion = false;
        var stopReason = ReadString(message, "stop_reason");
        if (string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
        {
            // ⚠️ A-K2 The nested stop_reason and final text shape are not confirmed on Steve's machine.
            state.Turn = ClaudeCodeTurnStatus.Completed;
            state.EndedWithQuestion = QuestionDetector.EndsWithQuestion(lastText);
            state.PendingTools.Clear();
        }
    }

    static void ApplyTitleRecord(JsonElement root, string propertyName, Action<string?> update)
    {
        // ⚠️ A-K2 Title record field names were not present in the redacted desktop write report.
        var candidate = TextSanitizer.SanitizeTitleCandidate(ReadString(root, propertyName));
        if (candidate is not null) update(candidate);
    }

    static bool IsSlashCommandWrapper(string text)
    {
        // ⚠️ A-K2 Slash-command wrapper shapes are inferred from transcript user-message conventions.
        if (text.StartsWith("<command-name>", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("<command-message>", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("<local-command-caveat>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstLine = text.Split('\n', 2)[0].TrimStart();
        return firstLine.StartsWith('/') && firstLine.Length > 1 &&
            !char.IsWhiteSpace(firstLine[1]);
    }

    static DateTimeOffset ReadTimestamp(JsonElement root, DateTimeOffset fallback) =>
        TryReadTimestamp(root, "timestamp", out var timestamp) || TryReadTimestamp(root, "ts", out timestamp)
            ? timestamp
            : fallback;

    static bool TryReadTimestamp(JsonElement element, string propertyName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out timestamp);
    }

    static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        var property = GetProperty(element, propertyName);
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    static string? ReadString(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName, out var value) ? value : null;

    static JsonElement GetObject(JsonElement element, string propertyName)
    {
        var value = GetProperty(element, propertyName);
        return value.ValueKind == JsonValueKind.Object ? value : default;
    }

    static JsonElement GetProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value
            : default;

    static bool IsTrue(JsonElement value) => value.ValueKind == JsonValueKind.True;
}
