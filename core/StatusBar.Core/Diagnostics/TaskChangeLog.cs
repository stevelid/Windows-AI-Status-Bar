using StatusBar.Core.Tasks;

namespace StatusBar.Core.Diagnostics;

/// <summary>
/// Turns two task-state snapshots into content-free log lines for debugging: short task ids,
/// status and confidence changes, the app's own fixed reason text, counts and provider health.
/// Titles are deliberately never written.
/// </summary>
public static class TaskChangeLog
{
    public static IReadOnlyList<string> Describe(StatusBarState previous, StatusBarState next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        var lines = new List<string>();
        var before = previous.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var after = next.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);

        foreach (var task in next.Tasks)
        {
            if (!before.TryGetValue(task.Id, out var old))
                lines.Add($"Task {ShortId(task.Id)} added: {State(task)}");
            else if (State(old) != State(task))
                lines.Add($"Task {ShortId(task.Id)} {State(old)} -> {State(task)}");
        }

        foreach (var task in previous.Tasks.Where(task => !after.ContainsKey(task.Id)))
            lines.Add($"Task {ShortId(task.Id)} removed (was {task.Status})");

        if (previous.WorkingCount != next.WorkingCount || previous.AttentionCount != next.AttentionCount)
        {
            lines.Add($"Counts: working {previous.WorkingCount} -> {next.WorkingCount}, " +
                      $"attention {previous.AttentionCount} -> {next.AttentionCount}");
        }

        foreach (var (provider, health) in next.TaskProviderHealth)
        {
            previous.TaskProviderHealth.TryGetValue(provider, out var oldHealth);
            if (oldHealth is null || oldHealth.State != health.State || oldHealth.Code != health.Code)
                lines.Add($"Tasks {provider}: {oldHealth?.State.ToString() ?? "none"}/{oldHealth?.Code ?? "-"} -> {health.State}/{health.Code}");
        }

        return lines;
    }

    // Status, confidence and the app's fixed reason/detail strings (never provider text).
    static string State(AgentTask task)
    {
        var text = $"{task.Status}/{task.Confidence}";
        if (task.AttentionReason is not null) text += $" ({task.AttentionReason})";
        if (task.StatusDetail is not null) text += $" [{task.StatusDetail}]";
        return text;
    }

    // "codex:0f8fad5b-d9cb-..." -> "codex:0f8fad5b"
    internal static string ShortId(string id)
    {
        var colon = id.IndexOf(':');
        var prefix = colon >= 0 ? id[..(colon + 1)] : "";
        var key = colon >= 0 ? id[(colon + 1)..] : id;
        return prefix + (key.Length > 8 ? key[..8] : key);
    }
}
