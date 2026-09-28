using System.IO;
using System.Text;
using StatusBar.Core.Claude;

namespace ClaudeUsageWidget;

/// <summary>Installs and removes the opt-in Claude Code command hooks.</summary>
internal static class ClaudeHookSettingsInstaller
{
    const string SettingsFileName = "settings.json";
    const string BackupSuffix = ".windows-ai-status-bar.bak";

    internal static string BuildPreview() =>
        ClaudeSettingsHookMerger.Add("{}", BuildCommandLine(Environment.ProcessPath));

    internal static void SetInstalled(bool enabled, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var home = ClaudeCodePaths.Resolve(
            settings.ClaudeCodeHomeOverride,
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")).Home;
        var path = Path.Combine(home, SettingsFileName);

        if (!enabled && !File.Exists(path)) return;
        if (enabled) Directory.CreateDirectory(home);

        var exists = File.Exists(path);
        var original = exists ? File.ReadAllText(path) : "{}";
        if (enabled && exists) BackupOnce(path);

        var updated = enabled
            ? ClaudeSettingsHookMerger.Add(original, BuildCommandLine(Environment.ProcessPath))
            : ClaudeSettingsHookMerger.Remove(original);
        if (string.Equals(updated, original, StringComparison.Ordinal)) return;

        WriteAtomically(path, updated);
    }

    internal static void EnsureCurrentCommand(Settings settings)
    {
        if (settings.UseClaudeCodeHooks) SetInstalled(enabled: true, settings: settings);
    }

    static string BuildCommandLine(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("The current executable path is unavailable.");
        var fullPath = Path.GetFullPath(executablePath);
        return $"\"{fullPath}\" --claude-hook";
    }

    static void BackupOnce(string settingsPath)
    {
        var backupPath = settingsPath + BackupSuffix;
        if (File.Exists(backupPath)) return;

        try
        {
            File.Copy(settingsPath, backupPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(backupPath))
        {
            // Another startup already made the one-time backup.
        }
    }

    static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Settings directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, SettingsFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, content + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
