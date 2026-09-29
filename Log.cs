using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeUsageWidget;

/// <summary>Minimal file logger for diagnosing startup issues (%APPDATA%\WindowsAIStatusBar\log.txt).</summary>
public static class Log
{
    static readonly object Gate = new();
    static string Dir => AppPaths.DataDir;
    public static string FilePath => Path.Combine(Dir, "log.txt");
    static string PreviousFilePath => Path.Combine(Dir, "log.old.txt");
    const long MaxBytes = 1024 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // Keep the log bounded but keep one previous file, so a problem that happened just
                // before a roll-over is still in log.old.txt.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, PreviousFilePath, overwrite: true);
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
