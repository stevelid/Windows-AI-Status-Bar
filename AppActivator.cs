using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StatusBar.Core.Claude;
using StatusBar.Core.Tasks;

namespace ClaudeUsageWidget;

/// <summary>Brings the desktop application that owns a task to the foreground.</summary>
internal static class AppActivator
{
    internal enum FocusResult { Failed, AppFocused, SessionLinkDispatched }
    const int SwRestore = 9;

    static readonly ActivationTarget Codex = new(
        "OpenAI.Codex_",
        "App",
        "codex");

    static readonly ActivationTarget Claude = new(
        "Claude_",
        "Claude",
        // ⚠️ A-C8: recon confirmed Claude Desktop's top-level process name.
        "claude");

    /// <summary>Opens the selected session, falling back to the owning app if Windows rejects its link.</summary>
    public static async Task<FocusResult> TryFocusAsync(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var target = task.Provider == AgentProvider.Codex ? Codex : Claude;

        string? desktopSession = null;
        if (task.Provider == AgentProvider.Claude && task.SessionReference is { } session)
        {
            // Reading desktop metadata off the UI thread keeps clicks responsive on synced disks.
            desktopSession = await Task.Run(() =>
                ClaudeDesktopSessionReader.FindSession(ClaudeCodeSessionRoots(), session));
        }
        var link = TaskNavigation.CreateLink(task, desktopSession);
        if (link is not null && TryOpenLink(link)) return FocusResult.SessionLinkDispatched;
        return TryActivatePackage(target) || TryFocusProcessWindow(target)
            ? FocusResult.AppFocused : FocusResult.Failed;
    }

    static bool TryOpenLink(Uri link)
    {
        try
        {
            // Windows hands the fixed provider protocol to its registered desktop app.
            // Dispatch success is not proof that the app accepted or navigated to the session.
            using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    static IEnumerable<string> ClaudeCodeSessionRoots()
    {
        // ⚠️ A-K7 Both ordinary installs and MSIX LocalCache can hold desktop Code metadata.
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions"),
        };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            if (Directory.Exists(packages))
                roots.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code-sessions")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missing package root leaves ordinary installs and the CLI resume link available.
        }
        return roots;
    }

    static bool TryActivatePackage(ActivationTarget target)
    {
        var packageFamily = ResolvePackageFamily(target.PackagePrefix);
        if (packageFamily is null) return false;

        var appUserModelId = $"{packageFamily}!{target.AppId}";
        IApplicationActivationManager? activationManager = null;
        try
        {
            activationManager = (IApplicationActivationManager)Activator.CreateInstance(
                typeof(ApplicationActivationManager))!;
            var result = activationManager.ActivateApplication(
                appUserModelId,
                null,
                ActivateOptions.None,
                out _);
            return result >= 0;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (activationManager is not null)
                Marshal.FinalReleaseComObject(activationManager);
        }
    }

    static string? ResolvePackageFamily(string packagePrefix)
    {
        try
        {
            var packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (!Directory.Exists(packageRoot)) return null;

            return Directory.EnumerateDirectories(packageRoot, packagePrefix + "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                    name.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool TryFocusProcessWindow(ActivationTarget target)
    {
        var processId = 0u;
        var window = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;

            GetWindowThreadProcessId(handle, out var candidateProcessId);
            if (candidateProcessId == 0 || !IsTargetProcess(candidateProcessId, target.ProcessName))
                return true;

            window = handle;
            processId = candidateProcessId;
            return false;
        }, IntPtr.Zero);

        if (window == IntPtr.Zero || processId == 0) return false;
        if (IsIconic(window)) ShowWindow(window, SwRestore);
        return SetForegroundWindow(window);
    }

    static bool IsTargetProcess(uint processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    readonly record struct ActivationTarget(string PackagePrefix, string AppId, string ProcessName);

    [Flags]
    enum ActivateOptions
    {
        None = 0,
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-AB7A-56EA9078943C")]
    sealed class ApplicationActivationManager
    {
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
