namespace StatusBar.Core.Tasks;

/// <summary>Builds fixed provider links from validated in-memory session references.</summary>
public static class TaskNavigation
{
    /// <summary>Returns a conversation link, or null when the task has no usable session identity.</summary>
    public static Uri? CreateLink(AgentTask task, string? claudeDesktopSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        // IDs are data, never caller-supplied URLs, commands, paths or query parameters.
        if (!Guid.TryParseExact(task.SessionReference, "D", out _)) return null;
        var session = Uri.EscapeDataString(task.SessionReference!);
        if (task.Provider == AgentProvider.Codex)
            // Documented: https://learn.chatgpt.com/docs/reference/commands#deep-links
            return new Uri("codex://threads/" + session);
        if (task.Provider != AgentProvider.Claude) return null;

        // ⚠️ A-K7 Desktop session IDs are distinct from CLI transcript IDs. The installed
        // handler accepts local_* IDs on /continue; /resume imports a CLI transcript.
        if (IsClaudeDesktopSessionId(claudeDesktopSessionId))
            return new Uri("claude://code/continue?session=" + Uri.EscapeDataString(claudeDesktopSessionId!));
        return new Uri("claude://resume?session=" + session);
    }

    /// <summary>Matches the desktop handler's local session ID grammar, excluding navigation verbs.</summary>
    public static bool IsClaudeDesktopSessionId(string? value) =>
        value is { Length: > 6 and <= 70 } && value.StartsWith("local_", StringComparison.Ordinal) &&
        value.AsSpan(6).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-".AsSpan()) < 0;
}
