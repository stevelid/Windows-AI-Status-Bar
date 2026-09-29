using System.IO.Compression;
using System.Text;

namespace StatusBar.Core.Diagnostics;

/// <summary>Writes the fixed, redacted diagnostic bundle entries.</summary>
public static class DiagnosticBundleWriter
{
    /// <summary>Creates or replaces a bundle containing only the three support text entries.</summary>
    public static void Write(string path, string report, string logTail, string formatDrift)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logTail);
        ArgumentNullException.ThrowIfNull(formatDrift);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        using var archive = new ZipArchive(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None),
            ZipArchiveMode.Create);
        AddText(archive, "report.txt", report);
        AddText(archive, "log-tail.txt", logTail);
        AddText(archive, "format-drift.txt", formatDrift);
    }

    static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
