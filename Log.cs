using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>Minimal file logger for diagnosing startup issues (%APPDATA%\WindowsAIStatusBar\log.txt).</summary>
public static class Log
{
    static readonly object Gate = new();
    static string Dir => AppPaths.DataDir;
    public static string FilePath => Path.Combine(Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // keep the log from growing unbounded
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Sanitize(message)}\r\n");
            }
        }
        catch { }
    }

    public static void Error(string context, Exception ex) => Write($"{context}: {ex.GetType().Name}");

    /// <summary>Reads the last log lines after applying the same content-free redaction used on write.</summary>
    public static string ReadDiagnosticTail(int maxLines = 300)
    {
        if (maxLines <= 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return "No log entries.\r\n";
                var lines = File.ReadLines(FilePath).TakeLast(maxLines).Select(Sanitize).ToArray();
                return lines.Length == 0 ? "No log entries.\r\n" : string.Join("\r\n", lines) + "\r\n";
            }
        }
        catch (Exception ex)
        {
            return $"Could not read log: {ex.GetType().Name}\r\n";
        }
    }

    static string Sanitize(string message)
    {
        var safe = message ?? "";
        foreach (var marker in new[] { "原始內容:", "original content:" })
        {
            var index = safe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) safe = safe[..index] + marker + " <redacted>";
        }

        safe = Regex.Replace(safe, @"(?i)args=\[[^\]]*\]", "args=[redacted]");
        safe = Regex.Replace(safe, @"(?i)(?:path=|->\s*)(?:[A-Za-z]:\\|\\\\)[^\r\n]*", "<path>");
        safe = Regex.Replace(safe, @"(?i)(?:[A-Za-z]:\\|\\\\)[^\s\]]+", "<path>");
        return safe.Length <= 4000 ? safe : safe[..4000];
    }
}
