using System.Globalization;
using System.Text.Json;
using StatusBar.Core.Common;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>A normalized page of cloud tasks; no summaries or transcript text are retained.</summary>
public sealed record CodexCloudTaskPage(IReadOnlyList<AgentTask> Tasks, string? Cursor);

/// <summary>Parses the documented machine-readable Codex cloud task list.</summary>
public static class CodexCloudTaskParser
{
    /// <summary>Normalizes one page. A malformed response fails the whole refresh.</summary>
    public static CodexCloudTaskPage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tasks = new List<AgentTask>();
        foreach (var item in root.GetProperty("tasks").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (!IsTaskId(id)) throw new JsonException("Invalid cloud task identity.");
            var updated = DateTimeOffset.Parse(item.GetProperty("updated_at").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            // ⚠️ A-X9: CLI enum semantics are Pending, Ready, Applied, Error.
            // Pending includes queued work; it does not prove the model is currently executing.
            var status = item.GetProperty("status").GetString() switch
            {
                "pending" => AgentTaskStatus.Working,
                "ready" or "applied" => AgentTaskStatus.Complete,
                "error" => AgentTaskStatus.Failed,
                _ => AgentTaskStatus.Unknown,
            };
            var title = item.TryGetProperty("title", out var name)
                ? TextSanitizer.SanitizeTitleCandidate(name.GetString()) : null;
            tasks.Add(new AgentTask
            {
                Id = "codex-cloud:" + id,
                Provider = AgentProvider.Codex,
                Title = title ?? "Codex Cloud",
                Status = status,
                Confidence = status == AgentTaskStatus.Unknown ? StateConfidence.Stale : StateConfidence.Confirmed,
                LastActivity = updated,
                StatusDetail = "Cloud",
                SessionReference = id,
                IsCloud = true,
                EvidenceKey = $"cloud:{status}:{updated.UtcTicks}",
            });
        }
        var cursor = root.TryGetProperty("cursor", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString() : null;
        return new CodexCloudTaskPage(tasks, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    /// <summary>Validates a cloud identity before using it in a fixed task link.</summary>
    public static bool IsTaskId(string? id) => id is { Length: > 5 and <= 100 } &&
        id.StartsWith("task_", StringComparison.Ordinal) &&
        id.AsSpan(5).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) < 0;
}
