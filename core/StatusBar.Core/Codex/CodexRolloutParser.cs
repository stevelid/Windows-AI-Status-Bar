using System.Text;
using System.Text.Json;
using StatusBar.Core.Common;
using StatusBar.Core.Diagnostics;

namespace StatusBar.Core.Codex;

/// <summary>Applies one Codex rollout record to the in-memory state for its session.</summary>
internal static class CodexRolloutParser
{
    internal static void Apply(
        CodexSessionState state,
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

            var payload = GetObject(root, "payload");
            var payloadType = TryGetString(payload, "type", out var nestedType) ? nestedType : null;
            state.LastActivity = ReadTimestamp(root, fallbackTime);
            state.HasActivity = true;

            switch (recordType)
            {
                case "session_meta":
                    ApplySessionMeta(state, payload);
                    return;
                case "turn_context":
                    state.ApprovalPolicy = ReadString(payload, "approval_policy");
                    return;
                case "event_msg":
                    ApplyEvent(state, payloadType, payload, drift, recordType);
                    return;
                case "response_item":
                    ApplyResponseItem(state, payloadType, payload, drift, recordType);
                    return;
                case "turn_started":
                    StartTurn(state);
                    return;
                case "turn_complete":
                    CompleteTurn(state, payload);
                    return;
                case "turn_aborted":
                    AbortTurn(state, payload);
                    return;
                case "world_state":
                case "token_usage_record":
                    return;
                default:
                    drift.RecordUnknown(GetSignature(recordType, payloadType));
                    return;
            }
        }
    }

    static void ApplySessionMeta(CodexSessionState state, JsonElement payload)
    {
        // ⚠️ A-X1 The rollout's session metadata carries its thread and working-directory fields.
        state.ThreadId = ReadString(payload, "id");
        state.Source = ReadString(payload, "source");
        if (IsSubAgent(state.Source))
        {
            // ⚠️ A-X6 Sub-agent parent metadata is not yet confirmed by live recon.
            state.ParentThreadId = ReadString(payload, "parent_thread_id") ?? ReadString(payload, "parentThreadId");
        }

        var cwd = ReadString(payload, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.TrimEnd('/', '\\');
            var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            state.CwdLeaf = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        }

        if (TryReadTimestamp(payload, "timestamp", out var startedAt))
            state.StartedAt = startedAt;
        else if (TryReadTimestamp(payload, "started_at", out startedAt))
            state.StartedAt = startedAt;
        else
            state.StartedAt = state.LastActivity;
    }

    static void ApplyEvent(
        CodexSessionState state,
        string? eventType,
        JsonElement payload,
        FormatDriftCounter drift,
        string recordType)
    {
        switch (eventType)
        {
            case "task_started":
            case "turn_started":
                // ⚠️ A-X2 Both event names are treated as explicit turn boundaries.
                StartTurn(state);
                return;
            case "task_complete":
            case "turn_complete":
                CompleteTurn(state, payload);
                return;
            case "turn_aborted":
                AbortTurn(state, payload);
                return;
            case "item_completed":
                ApplyCompletedItem(state, payload);
                return;
            case "token_count":
            case "thread_settings_applied":
                return;
            default:
                drift.RecordUnknown(GetSignature(recordType, eventType));
                return;
        }
    }

    static void ApplyCompletedItem(CodexSessionState state, JsonElement payload)
    {
        var item = GetObject(payload, "item");
        if (string.Equals(ReadString(item, "type"), "UserMessage", StringComparison.OrdinalIgnoreCase) &&
            state.TitleCandidate is null)
        {
            state.TitleCandidate = ReadTitleCandidate(item, "content");
        }
    }

    static void ApplyResponseItem(
        CodexSessionState state,
        string? itemType,
        JsonElement payload,
        FormatDriftCounter drift,
        string recordType)
    {
        switch (itemType)
        {
            case "function_call":
                AddPendingCall(state, payload);
                return;
            case "function_call_output":
            case "custom_tool_call_output":
                RemovePendingCall(state, payload);
                return;
            case "custom_tool_call":
                // Activity only. Deliberately do not inspect or retain the free-form input field. ⚠️ A-X2
                return;
            case "message":
                if (state.TitleCandidate is null &&
                    string.Equals(ReadString(payload, "role"), "user", StringComparison.Ordinal))
                {
                    state.TitleCandidate = ReadTitleCandidate(payload, "content");
                }
                return;
            default:
                drift.RecordUnknown(GetSignature(recordType, itemType));
                return;
        }
    }

    static void AddPendingCall(CodexSessionState state, JsonElement payload)
    {
        var callId = ReadString(payload, "call_id");
        if (string.IsNullOrWhiteSpace(callId)) return;

        var name = ReadString(payload, "name");
        if (string.Equals(name, "request_user_input", StringComparison.Ordinal))
        {
            // ⚠️ A-X4 This pending input request record remains unverified on Steve's setup.
            state.PendingCalls[callId] = new CodexPendingCall(callId, CodexPendingKind.Input, state.LastActivity);
            return;
        }

        var arguments = GetProperty(payload, "arguments");
        if (HasEscalatedSandboxPermission(arguments))
        {
            // This field is an undocumented persisted signal; the exact permission marker is all we inspect. ⚠️ A-X3
            state.PendingCalls[callId] = new CodexPendingCall(callId, CodexPendingKind.Approval, state.LastActivity);
        }
    }

    static bool HasEscalatedSandboxPermission(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
        {
            var json = arguments.GetString();
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                return HasEscalatedSandboxPermission(parsed.RootElement);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return arguments.ValueKind == JsonValueKind.Object &&
            string.Equals(ReadString(arguments, "sandbox_permissions"), "require_escalated", StringComparison.Ordinal);
    }

    static void RemovePendingCall(CodexSessionState state, JsonElement payload)
    {
        var callId = ReadString(payload, "call_id");
        if (!string.IsNullOrEmpty(callId)) state.PendingCalls.Remove(callId);
    }

    static void StartTurn(CodexSessionState state)
    {
        state.Turn = CodexTurnStatus.Running;
        state.HasSeenTurnEvent = true;
        state.EndedWithQuestion = false;
        state.TurnId = null;
        state.AbortReason = null;
        state.PendingCalls.Clear();
    }

    static void CompleteTurn(CodexSessionState state, JsonElement payload)
    {
        state.HasSeenTurnEvent = true;
        state.TurnId = ReadString(payload, "turn_id") ?? ReadString(payload, "id");
        var error = GetProperty(payload, "error");
        if (error.ValueKind == JsonValueKind.Object)
        {
            // ⚠️ A-X8 A non-null error object distinguishes failed turns; never inspect its message.
            state.Turn = CodexTurnStatus.Failed;
            state.EndedWithQuestion = false;
        }
        else
        {
            state.Turn = CodexTurnStatus.Completed;
            // D15: reduce the terminal message to one boolean and discard the text with this JSON record.
            state.EndedWithQuestion = QuestionDetector.EndsWithQuestion(ReadString(payload, "last_agent_message"));
        }
        state.PendingCalls.Clear();
    }

    static void AbortTurn(CodexSessionState state, JsonElement payload)
    {
        state.HasSeenTurnEvent = true;
        state.Turn = CodexTurnStatus.Aborted;
        state.EndedWithQuestion = false;
        state.AbortReason = NormalizeAbortReason(ReadString(payload, "reason"));
        state.PendingCalls.Clear();
    }

    static string? ReadTitleCandidate(JsonElement source, string propertyName)
    {
        var content = GetProperty(source, propertyName);
        var builder = new StringBuilder();
        if (content.ValueKind == JsonValueKind.String)
        {
            builder.Append(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String)
                {
                    AppendTextBlock(builder, block.GetString());
                    continue;
                }
                if (block.ValueKind == JsonValueKind.Object && TryGetString(block, "text", out var text))
                    AppendTextBlock(builder, text);
            }
        }
        else if (content.ValueKind == JsonValueKind.Object && TryGetString(content, "text", out var nestedText))
        {
            builder.Append(nestedText);
        }

        return SanitizeTitleCandidate(builder.ToString());
    }

    static void AppendTextBlock(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (builder.Length > 0) builder.Append(' ');
        builder.Append(text);
    }

    static string? SanitizeTitleCandidate(string text)
    {
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0 || IsInjectedContext(firstLine)) return null;

        var normalized = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = normalized.TrimStart('#', '>', '-', '*', '_', '`').Trim();
        if (normalized.Length == 0) return null;
        if (normalized.Length <= 48) return normalized;

        var limit = 47;
        var boundary = normalized.LastIndexOf(' ', limit);
        if (boundary > 0) limit = boundary;
        return normalized[..limit].TrimEnd() + "…";
    }

    static bool IsInjectedContext(string text) =>
        text.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("<user_instructions>", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("<permissions", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase);

    static DateTimeOffset ReadTimestamp(JsonElement root, DateTimeOffset fallback) =>
        TryReadTimestamp(root, "timestamp", out var timestamp) || TryReadTimestamp(root, "ts", out timestamp)
            ? timestamp
            : fallback;

    static bool TryReadTimestamp(JsonElement element, string propertyName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = GetProperty(element, propertyName);
        if (value.ValueKind != JsonValueKind.String) return false;
        return DateTimeOffset.TryParse(value.GetString(), out timestamp);
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

    static string GetSignature(string recordType, string? nestedType) =>
        string.IsNullOrWhiteSpace(nestedType) ? recordType : $"{recordType}/{nestedType}";

    static bool IsSubAgent(string? source) =>
        string.Equals(source, "sub-agent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "sub_agent", StringComparison.OrdinalIgnoreCase);

    static string? NormalizeAbortReason(string? reason) => reason switch
    {
        "interrupted" => "interrupted",
        "replaced" => "replaced",
        "review_ended" => "review_ended",
        "budget_limited" => "budget_limited",
        null => null,
        _ => "other",
    };
}
