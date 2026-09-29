using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUsageWidget;

/// <summary>Matches standard-chrome windows (Settings, sign-in) to the app theme and icon.</summary>
static class WindowTheming
{
    const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static ImageSource? _icon;

    /// <summary>Sets the app icon now and the title-bar theme once the window has a handle.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Icon = AppIcon();
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyTitleBar(window);
        else
            window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    /// <summary>Re-applies the title-bar theme after the user switches between light and dark.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = ThemeManager.IsLight ? 0 : 1;
        try
        {
            // Supported on Windows 10 20H1 and later; older builds ignore the attribute.
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    static ImageSource? AppIcon()
    {
        if (_icon is not null) return _icon;
        try
        {
            using var icon = TrayIconRenderer.Render(null);
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _icon = source;
        }
        catch (Exception ex)
        {
            Log.Error("Window icon", ex);
        }
        return _icon;
    }
}
