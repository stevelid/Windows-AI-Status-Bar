using System.IO;
using StatusBar.Core.Common;

namespace ClaudeUsageWidget;

/// <summary>
/// Resolves the app data directory once, defensively. During early logon
/// Environment.GetFolderPath can fail or return "", which silently broke token
/// loading and logging — so we fall back through env vars to the exe directory.
/// </summary>
public static class AppPaths
{
    public static string ResolutionNote { get; private set; } = "";

    static string BaseDir { get; } = ResolveBaseDir();

    public static string DataDir { get; } = Path.Combine(BaseDir, "WindowsAIStatusBar");

    static string ResolveBaseDir()
    {
        string? baseDir = null;
        try
        {
            baseDir = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
        }
        catch { }

        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Environment.GetEnvironmentVariable("APPDATA");
            ResolutionNote = "GetFolderPath 失敗，改用 %APPDATA%";
        }
        if (string.IsNullOrEmpty(baseDir))
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(profile))
            {
                baseDir = Path.Combine(profile, "AppData", "Roaming");
                ResolutionNote = "GetFolderPath 與 %APPDATA% 皆失敗，改用 %USERPROFILE%";
            }
        }
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = AppContext.BaseDirectory;
            ResolutionNote = "所有使用者路徑皆失敗，改用執行檔目錄";
        }

        return baseDir;
    }

    /// <summary>Creates the new data folder and imports the legacy encrypted token file once.</summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(DataDir);

        var oldDir = Path.Combine(BaseDir, "ClaudeUsageWidget");
        var markerPath = Path.Combine(DataDir, DataFolderMigration.ImportCompletedMarkerFileName);
        if (File.Exists(markerPath)) return;

        var oldTokenPath = Path.Combine(oldDir, "tokens.dat");
        var newTokenPath = Path.Combine(DataDir, "tokens.dat");
        var legacyTokenExists = File.Exists(oldTokenPath);
        var importComplete = legacyTokenExists && File.Exists(newTokenPath);

        foreach (var file in DataFolderMigration.Plan(oldDir, DataDir, File.Exists))
        {
            try
            {
                File.Copy(file.SourcePath, file.DestinationPath, overwrite: false);
                importComplete = true;
            }
            catch (IOException) when (File.Exists(file.DestinationPath))
            {
                // Another launch created the new token file after the migration plan was read.
                importComplete = true;
            }
            catch (Exception ex)
            {
                // Do not include per-user filesystem paths in the log.
                Log.Write($"Legacy Claude token import failed: {ex.GetType().Name}");
            }
        }

        if (!importComplete) return;

        try
        {
            // The old folder stays for the upstream app; this prevents logout from importing the token again.
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(markerPath))
        {
            // Another launch recorded the completed migration.
        }
        catch (Exception ex)
        {
            Log.Write($"Legacy Claude token import marker failed: {ex.GetType().Name}");
        }
    }
}
