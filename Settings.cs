using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

public class Settings
{
    [Obsolete("Kept for settings.json compatibility; version 3 uses monitor docking.")]
    public double? WindowLeft { get; set; }
    [Obsolete("Kept for settings.json compatibility; version 3 uses monitor docking.")]
    public double? WindowTop { get; set; }
    public bool WidgetVisible { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string? Language { get; set; }            // "zh" | "en" | null = follow system
    public string Theme { get; set; } = "dark";      // "dark" | "light"
    public int BgTransparency { get; set; } = 10;    // 0 = solid, 100 = fully transparent
    public int RefreshIntervalSec { get; set; } = 90;
    [Obsolete("Kept for settings.json compatibility; the details pane is toggled directly.")]
    public bool Collapsed { get; set; }
    public double UiScale { get; set; } = 1.0;   // 0.7–2.5, drag widget edges to change
    [Obsolete("Kept for settings.json compatibility; use the tray Sign in submenu.")]
    public string ActiveProvider { get; set; } = "claude";
    public string? CodexExecutablePath { get; set; }
    public string? CodexHomeOverride { get; set; }
    public string? ClaudeCodeHomeOverride { get; set; }
    public string? CoworkRootOverride { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public int RecentlyCompletedMinutes { get; set; } = 10;
    public int PaneAutoCollapseSeconds { get; set; } = 0;
    public string AttentionLabel { get; set; } = "STEVE";
    public string? MonitorDeviceName { get; set; }
    public int ApproachingBelowPercent { get; set; } = 30;
    public int LowBelowPercent { get; set; } = 10;
    public bool DemoTasks { get; set; } = false;
    [JsonIgnore]
    public bool DoNotPersist { get; set; }

    static string Dir => AppPaths.DataDir;
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                settings.Normalize();
                return settings;
            }
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        if (DoNotPersist) return;
        Normalize();
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    /// <summary>Clamps settings values that affect thresholds, intervals, or display geometry.</summary>
    public void Normalize()
    {
        BgTransparency = Math.Clamp(BgTransparency, 0, 100);
        RefreshIntervalSec = Math.Clamp(RefreshIntervalSec, 30, 3600);
        UiScale = double.IsFinite(UiScale) ? Math.Clamp(UiScale, 0.7, 2.5) : 1.0;
        RecentlyCompletedMinutes = Math.Clamp(RecentlyCompletedMinutes, 5, 15);
        PaneAutoCollapseSeconds = Math.Max(0, PaneAutoCollapseSeconds);
        LowBelowPercent = Math.Clamp(LowBelowPercent, 0, 99);
        ApproachingBelowPercent = Math.Clamp(ApproachingBelowPercent, 1, 100);
        if (ApproachingBelowPercent <= LowBelowPercent)
            ApproachingBelowPercent = LowBelowPercent + 1;
        AttentionLabel = string.IsNullOrWhiteSpace(AttentionLabel) ? "STEVE" : AttentionLabel.Trim();
        MonitorDeviceName = NormalizeOptionalPath(MonitorDeviceName);
        CodexExecutablePath = NormalizeOptionalPath(CodexExecutablePath);
        CodexHomeOverride = NormalizeOptionalPath(CodexHomeOverride);
        ClaudeCodeHomeOverride = NormalizeOptionalPath(ClaudeCodeHomeOverride);
        CoworkRootOverride = NormalizeOptionalPath(CoworkRootOverride);
    }

    static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AutoStartResult(bool Succeeded, string? Detail = null);

public static class AutoStart
{
    // Legacy mechanism (v1 used HKCU Run; on this machine Windows silently ignored the
    // entry at logon, so we switched to a Startup-folder shortcut).
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeUsageWidget"; // legacy Run value; removed on enable/disable

    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WindowsAIStatusBar.lnk");

    public static bool IsEnabled()
    {
        if (File.Exists(ShortcutPath)) return true;
        try
        {
            return RunKeyExists();
        }
        catch (Exception ex)
        {
            Log.Error("Could not inspect the auto-start registry entry", ex);
            return false;
        }
    }

    public static AutoStartResult TryEnable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return new AutoStartResult(false, "ProcessPathUnavailable");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            var lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = exe;
            lnk.Arguments = "--autostart";
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.Description = "Windows AI Status Bar";
            lnk.Save();
            Log.Write($"Created auto-start shortcut: {ShortcutPath} -> {exe}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the auto-start shortcut", ex);
            return new AutoStartResult(false, SafeFailureCode(ex));
        }

        // A policy-blocked registry cleanup is reported but does not invalidate
        // the shortcut that was successfully created.
        return TryRemoveRunKey(out var warning)
            ? new AutoStartResult(true)
            : new AutoStartResult(true, warning);
    }

    public static AutoStartResult TryDisable()
    {
        string? shortcutFailure = null;
        try
        {
            File.Delete(ShortcutPath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove the auto-start shortcut", ex);
            shortcutFailure = SafeFailureCode(ex);
        }

        var registryRemoved = TryRemoveRunKey(out var registryFailure);
        var succeeded = shortcutFailure is null && registryRemoved;
        var detail = string.Join(
            ",",
            new[] { shortcutFailure, registryFailure }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new AutoStartResult(succeeded, detail.Length == 0 ? null : detail);
    }

    static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    static bool TryRemoveRunKey(out string? failure)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove the legacy auto-start registry entry", ex);
            failure = SafeFailureCode(ex);
            return false;
        }
    }

    internal static string SafeFailureCode(Exception ex) =>
        $"{ex.GetType().Name}:0x{ex.HResult:X8}";
}
