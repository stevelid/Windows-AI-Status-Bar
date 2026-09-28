using System.IO;

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
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
            }
        }
        catch { }
    }

    public static void Error(string context, Exception ex) => Write($"{context}: {ex}");
}
