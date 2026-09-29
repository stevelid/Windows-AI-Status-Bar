using System.Text.Json;
using System.Text.RegularExpressions;
using StatusBar.Core.Common;
using StatusBar.Core.Diagnostics;

namespace StatusBar.Core.Claude;

/// <summary>Applies one Claude Code transcript record to its in-memory session state.</summary>
internal static class ClaudeCodeTranscriptParser
{
    const string InterruptedPrefix = "[Request interrupted by user";

    // ⚠️ A-K6 Claude Code reports a finished background agent or shell as a user record with
    // origin.kind "task-notification" whose text wraps the id in <task-id>…</task-id>. Only the id is read.
    static readonly Regex NotificationTaskId = new("<task-id>([^<]{1,128})</task-id>", RegexOptions.CultureInvariant);

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
                // ⚠️ A-K6 A subagent transcript is finished when its last assistant record ends the turn;
                // the parent stays busy while it is still being written.
                if (recordType == "assistant")
                    state.SidechainEnded = string.Equals(
                        ReadString(GetObject(root, "message"), "stop_reason"), "end_turn", StringComparison.Ordinal);
                else if (recordType == "user")
                    state.SidechainEnded = false;
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
                case "queue-operation":
                    // ⚠️ A-K6 A finished background task is queued as a notification. If Claude is mid-turn
                    // it is absorbed there and never becomes a user record, so the queue entry is the signal.
                    if (string.Equals(ReadString(root, "operation"), "enqueue", StringComparison.Ordinal))
                        ClearNotifiedBackgroundTask(state, ReadString(root, "content"));
                    return;
                case "attachment":
                    var attachment = GetObject(root, "attachment");
                    if (string.Equals(ReadString(GetObject(attachment, "origin"), "kind"), "task-notification", StringComparison.Ordinal))
                        ClearNotifiedBackgroundTask(state, ReadString(attachment, "prompt"));
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
        ApplyBackgroundLaunch(state, root);
        if (IsTrue(GetProperty(root, "isMeta")) || IsTrue(GetProperty(message, "isMeta"))) return;
        var isTaskNotification = string.Equals(
            ReadString(GetObject(root, "origin"), "kind"), "task-notification", StringComparison.Ordinal);

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

        if (isTaskNotification) ClearNotifiedBackgroundTask(state, firstText);

        if (hasToolResult || string.IsNullOrWhiteSpace(firstText)) return;

        var text = firstText.TrimStart();
        if (text.StartsWith(InterruptedPrefix, StringComparison.Ordinal))
        {
            // ⚠️ A-K3 This interruption marker is unverified and remains a provisional signal.
            state.HasSeenTurnEvent = true;
            state.HasNonSidechainActivity = true;
            state.Turn = ClaudeCodeTurnStatus.Aborted;
            state.TurnEndedAt = state.LastActivity;
            state.EndedWithQuestion = false;
            state.PendingTools.Clear();
            return;
        }

        if (IsSlashCommandWrapper(text)) return;

        state.HasSeenTurnEvent = true;
        state.HasNonSidechainActivity = true;
        state.Turn = ClaudeCodeTurnStatus.Running;
        state.TurnEndedAt = null;
        state.EndedWithQuestion = false;
        state.PendingTools.Clear();
        // A notification wakes Claude for a new turn, but its wrapper is not something Steve typed.
        if (state.FirstPromptTitleCandidate is null && !isTaskNotification)
            state.FirstPromptTitleCandidate = TextSanitizer.SanitizeTitleCandidate(firstText);
    }

    // Only the id inside Claude Code's own notification wrapper is read; the summary is ignored.
    static void ClearNotifiedBackgroundTask(ClaudeCodeSessionState state, string? text)
    {
        if (state.BackgroundTasks.Count == 0 || text is null) return;
        if (!text.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal)) return;
        var match = NotificationTaskId.Match(text);
        if (match.Success) state.BackgroundTasks.Remove(match.Groups[1].Value.Trim());
    }

    // ⚠️ A-K6 An Agent call with run_in_background returns at once with toolUseResult.status
    // "async_launched" and an agentId; a background shell returns a backgroundTaskId. The turn can then
    // end while the work continues, so the ids are kept until the matching task-notification arrives.
    static void ApplyBackgroundLaunch(ClaudeCodeSessionState state, JsonElement root)
    {
        var result = GetObject(root, "toolUseResult");
        if (result.ValueKind != JsonValueKind.Object) return;
        var id = string.Equals(ReadString(result, "status"), "async_launched", StringComparison.Ordinal)
            ? ReadString(result, "agentId")
            : ReadString(result, "backgroundTaskId");
        if (!string.IsNullOrWhiteSpace(id) && id.Length <= 128)
            state.BackgroundTasks[id] = state.LastActivity;
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
        state.TurnEndedAt = null;
        state.EndedWithQuestion = false;
        var stopReason = ReadString(message, "stop_reason");
        if (string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
        {
            // ⚠️ A-K2 The nested stop_reason and final text shape are not confirmed on Steve's machine.
            state.Turn = ClaudeCodeTurnStatus.Completed;
            state.TurnEndedAt = state.LastActivity;
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
