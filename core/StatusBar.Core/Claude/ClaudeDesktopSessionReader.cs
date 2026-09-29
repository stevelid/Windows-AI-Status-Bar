using System.Text.Json;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Claude;

/// <summary>Resolves a transcript ID to its existing desktop Code session without retaining content.</summary>
public static class ClaudeDesktopSessionReader
{
    const long MaximumMetadataBytes = 1024 * 1024;

    /// <summary>Looks in caller-supplied claude-code-sessions roots; unreadable files are skipped.</summary>
    public static string? FindSession(IEnumerable<string> roots, string cliSessionId)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!Guid.TryParseExact(cliSessionId, "D", out _)) return null;
        string? selected = null;
        long latestActivity = long.MinValue;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // ⚠️ A-K7 Observed layout: root/account/workspace/local_*.json. Restrict depth
            // so an app update cannot make this recurse through transcripts or outputs.
            foreach (var account in Directories(root))
            foreach (var workspace in Directories(account))
            foreach (var file in MetadataFiles(workspace))
            {
                var candidate = ReadSession(file, cliSessionId);
                if (candidate is { } session && (selected is null || session.LastActivity > latestActivity))
                {
                    selected = session.Id;
                    latestActivity = session.LastActivity;
                }
            }
        }
        return selected;
    }

    static string[] Directories(string directory)
    {
        try { return Directory.GetDirectories(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    static string[] MetadataFiles(string directory)
    {
        try { return Directory.GetFiles(directory, "local_*.json"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    static (string Id, long LastActivity)? ReadSession(string file, string cliSessionId)
    {
        try
        {
            if (new FileInfo(file).Length > MaximumMetadataBytes) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var metadata = document.RootElement;
            if (metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("cliSessionId", out var cli) || cli.ValueKind != JsonValueKind.String ||
                !string.Equals(cli.GetString(), cliSessionId, StringComparison.OrdinalIgnoreCase) ||
                metadata.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True ||
                !metadata.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            var session = id.GetString();
            // ⚠️ A-K7 If both installs retain a copy, prefer actual activity, not file write
            // time (app migrations can rewrite every metadata file at once).
            var activity = metadata.TryGetProperty("lastActivityAt", out var last) &&
                last.ValueKind == JsonValueKind.Number && last.TryGetInt64(out var timestamp) ? timestamp : 0;
            return TaskNavigation.IsClaudeDesktopSessionId(session) ? (session!, activity) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
