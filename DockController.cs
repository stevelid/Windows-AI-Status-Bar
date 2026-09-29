using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CoreRect = StatusBar.Core.Docking.Rect;
using CoreSize = StatusBar.Core.Docking.Size;
using RelativePosition = StatusBar.Core.Docking.RelativePosition;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageWidget;

/// <summary>Connects the status strip to Windows monitor, taskbar, and DPI changes.</summary>
public sealed class DockController : IDisposable
{
    const int WmSettingChange = 0x001A;
    const int WmDisplayChange = 0x007E;
    const int WmDpiChanged = 0x02E0;
    const double MarginDip = 12;

    static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    readonly StatusStripWindow _window;
    readonly Settings _settings;
    HwndSource? _source;
    bool _docking;
    bool _dragging;
    bool _disposed;

    /// <summary>Creates a dock controller and subscribes to the window lifecycle.</summary>
    public DockController(StatusStripWindow window, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        _window = window;
        _settings = settings;
        _window.SourceInitialized += OnSourceInitialized;
        _window.Loaded += OnLoaded;
        _window.SizeChanged += OnSizeChanged;
        _window.DragStarted += OnDragStarted;
        _window.DragCompleted += OnDragCompleted;
        _window.Closed += OnClosed;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Re-docks to the configured monitor, falling back to the primary display.</summary>
    public void Redock() => QueueDock(PreferredScreen());

    /// <summary>Unsubscribes from Windows events and removes the native message hook.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.SizeChanged -= OnSizeChanged;
        _window.DragStarted -= OnDragStarted;
        _window.DragCompleted -= OnDragCompleted;
        _window.Closed -= OnClosed;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
        QueueDock(PreferredScreen());
    }

    void OnLoaded(object sender, RoutedEventArgs e) => QueueDock(PreferredScreen());

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_docking && !_dragging) QueueDock(PreferredScreen());
    }

    void OnDragStarted() => _dragging = true;

    void OnDragCompleted()
    {
        _dragging = false;
        if (_disposed) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        var screen = WinForms.Screen.FromHandle(handle);
        var workArea = WorkAreaDip(screen);
        var size = StripSizeDip();
        var relative = StatusBar.Core.Docking.DockGeometry.CaptureRelative(
            workArea, size, _window.Left, _window.Top);
        _settings.MonitorDeviceName = screen.DeviceName;
        _settings.StripRelativeX = relative.X;
        _settings.StripRelativeY = relative.Y;
        _settings.Save();
        QueueDock(screen);
    }

    void OnClosed(object? sender, EventArgs e) => Dispose();

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueDock(PreferredScreen());
    }

    IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var registeredMessage = TaskbarCreatedMessage != 0 && unchecked((uint)message) == TaskbarCreatedMessage;
        if (message is WmDisplayChange or WmDpiChanged or WmSettingChange || registeredMessage)
        {
            // Windows updates work-area and per-monitor DPI values after these messages return.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => QueueDock(PreferredScreen())));
        }
        return IntPtr.Zero;
    }

    WinForms.Screen? PreferredScreen()
    {
        var screens = WinForms.Screen.AllScreens;
        if (!string.IsNullOrWhiteSpace(_settings.MonitorDeviceName))
        {
            var configured = screens.FirstOrDefault(screen => string.Equals(
                screen.DeviceName,
                _settings.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
            if (configured is not null) return configured;
        }

        // Keep the configured device name untouched so a disconnected monitor is
        // selected again automatically when it returns.
        return WinForms.Screen.PrimaryScreen ?? screens.FirstOrDefault();
    }

    void QueueDock(WinForms.Screen? targetScreen)
    {
        if (_disposed) return;
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => DockToScreen(targetScreen)));
            return;
        }
        DockToScreen(targetScreen);
    }

    void DockToScreen(WinForms.Screen? targetScreen)
    {
        if (_disposed || _docking || _dragging || !_window.IsLoaded) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        _window.UpdateLayout();
        var screen = targetScreen ?? WinForms.Screen.FromHandle(handle);
        var workAreaDip = WorkAreaDip(screen);
        var stripDip = StripSizeDip();
        var placed = _settings.StripRelativeX is double x && _settings.StripRelativeY is double y
            ? StatusBar.Core.Docking.DockGeometry.PlaceRelative(
                workAreaDip, stripDip, new RelativePosition(x, y))
            : StatusBar.Core.Docking.DockGeometry.Place(
                workAreaDip, stripDip,
                StatusBar.Core.Docking.DockAnchor.BottomRight, MarginDip);

        _docking = true;
        try
        {
            _window.Left = placed.X;
            _window.Top = placed.Y;
        }
        finally
        {
            _docking = false;
        }
    }

    CoreRect WorkAreaDip(WinForms.Screen screen)
    {
        var physical = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(_window);
        return new CoreRect(
            physical.Left / dpi.DpiScaleX,
            physical.Top / dpi.DpiScaleY,
            physical.Width / dpi.DpiScaleX,
            physical.Height / dpi.DpiScaleY);
    }

    CoreSize StripSizeDip() => new(
        Math.Max(1, _window.ActualWidth),
        Math.Max(1, _window.ActualHeight));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint RegisterWindowMessage(string message);
}
