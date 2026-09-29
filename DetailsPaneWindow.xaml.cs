using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CoreRect = StatusBar.Core.Docking.Rect;
using CoreSize = StatusBar.Core.Docking.Size;
using StatusBar.Core.Docking;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using WinForms = System.Windows.Forms;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>Shows provider quota windows and the merged task state above the status strip.</summary>
public partial class DetailsPaneWindow : Window
{
    const double PaneGapDip = 8;

    readonly Settings _settings;
    readonly Dictionary<string, UsageRow> _usageRows = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskRowView> _taskRows = new(StringComparer.Ordinal);
    readonly DispatcherTimer _countdownTimer;
    readonly DispatcherTimer _autoCollapseTimer;
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _usage =
        new Dictionary<UsageSource, UsageSnapshot>();
    StatusBarState _tasks = StatusBarState.Empty;
    bool _closeRequested;

    /// <summary>Raised when the user dismisses an inferred attention or unknown task row.</summary>
    public event Action<string>? DismissTaskRequested;

    /// <summary>Raised when the user clicks a task row to focus its owning desktop app.</summary>
    public event Action<AgentTask>? FocusTaskRequested;

    /// <summary>Creates the pane and starts its display timers only while it is visible.</summary>
    public DetailsPaneWindow(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        InitializeComponent();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += OnCountdownTick;
        _autoCollapseTimer = new DispatcherTimer();
        _autoCollapseTimer.Tick += OnAutoCollapseTick;
        IsVisibleChanged += OnVisibilityChanged;
        Closed += OnClosed;
        L10n.Changed += ApplyAppearance;
        ThemeManager.Changed += ApplyAppearance;
        ConfigureAutoCollapseTimer();
        ApplyAppearance();
    }

    /// <summary>Updates the immutable provider and task snapshots shown in the pane.</summary>
    public void UpdateState(
        IReadOnlyDictionary<UsageSource, UsageSnapshot> usage,
        StatusBarState tasks)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(tasks);
        _usage = usage;
        _tasks = tasks;
        RenderUsage(UsageSource.Codex, CodexUsageRows, CodexHealth);
        RenderUsage(UsageSource.Claude, ClaudeUsageRows, ClaudeHealth);
        RenderTasks();
    }

    /// <summary>Shows the pane above its owner and clamps it to that monitor's work area.</summary>
    public void ShowAbove(StatusStripWindow strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        Owner = strip;
        _closeRequested = false;
        Show();
        UpdateLayout();

        var stripHandle = new System.Windows.Interop.WindowInteropHelper(strip).Handle;
        if (stripHandle == IntPtr.Zero) return;
        var screen = WinForms.Screen.FromHandle(stripHandle);
        var dpi = VisualTreeHelper.GetDpi(strip);
        var physicalWorkArea = screen.WorkingArea;
        var workAreaDip = new CoreRect(
            physicalWorkArea.Left / dpi.DpiScaleX,
            physicalWorkArea.Top / dpi.DpiScaleY,
            physicalWorkArea.Width / dpi.DpiScaleX,
            physicalWorkArea.Height / dpi.DpiScaleY);
        var stripRect = new CoreRect(strip.Left, strip.Top, strip.ActualWidth, strip.ActualHeight);
        var placed = DockGeometry.PlacePaneAbove(
            stripRect,
            new CoreSize(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)),
            workAreaDip,
            PaneGapDip);
        Left = placed.X;
        Top = placed.Y;
        Activate();
        Focus();
    }

    /// <summary>Reapplies current language, theme and transparency settings.</summary>
    public void ApplyAppearance()
    {
        if (RootBorder is null) return;
        // The pane holds text to read, so it stays nearly opaque even when the strip is see-through.
        var alpha = (byte)Math.Clamp(
            (int)Math.Round(255 * (100 - _settings.BgTransparency) / 100.0), 248, 255);
        var surface = ThemeManager.IsLight
            ? Color.FromRgb(0xF3, 0xF3, 0xF7)
            : Color.FromRgb(0x1B, 0x1B, 0x24);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
        RootBorder.BorderBrush = ThemeManager.Brush(ThemeManager.IsLight
            ? Color.FromRgb(0xB8, 0xB9, 0xC3)
            : Color.FromRgb(0x5A, 0x5B, 0x67));
        PaneShadow.Color = ThemeManager.IsLight ? Color.FromRgb(0x42, 0x45, 0x52) : Colors.Black;
        PaneShadow.Opacity = ThemeManager.IsLight ? 0.24 : 0.42;
        TaskDivider.Background = ThemeManager.Brush(ThemeManager.IsLight
            ? Color.FromRgb(0xD8, 0xD9, 0xE0)
            : Color.FromRgb(0x4B, 0x4C, 0x56));
        ApplyHeadingStyle();
        ConfigureAutoCollapseTimer();
        UpdateState(_usage, _tasks);
    }

    void ApplyHeadingStyle()
    {
        var headingBrush = ThemeManager.Brush(ThemeManager.LabelText);
        foreach (var heading in new[]
                 {
                     CodexHeading, ClaudeHeading, NeedsYouHeading,
                     WorkingHeading, UnknownHeading, RecentHeading,
                 })
        {
            heading.Foreground = headingBrush;
        }
        EmptyTasks.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
        CodexHeading.Text = L10n.T("pane_codex");
        ClaudeHeading.Text = L10n.T("pane_claude");
        NeedsYouHeading.Text = L10n.T("pane_needs_you");
        WorkingHeading.Text = L10n.T("pane_working");
        UnknownHeading.Text = L10n.T("pane_unknown");
        RecentHeading.Text = L10n.T("pane_recent");
        EmptyTasks.Text = L10n.T("pane_empty_tasks");
    }

    void RenderUsage(UsageSource source, StackPanel rows, TextBlock healthText)
    {
        rows.Children.Clear();
        healthText.Text = "";
        healthText.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);

        if (!_usage.TryGetValue(source, out var snapshot))
        {
            AddUsageMessage(rows, L10n.T("err_usage_unavailable"));
            return;
        }

        if (snapshot.Health == UsageHealth.SignedOut)
        {
            RemoveUnusedUsageRows(source, new HashSet<string>(StringComparer.Ordinal));
            AddUsageMessage(rows, source == UsageSource.Claude
                ? L10n.T("err_not_signed_in_hint")
                : L10n.T("err_chatgpt_not_signed_in"));
            return;
        }

        if (snapshot.Windows.Count == 0)
        {
            var explanation = snapshot.Health switch
            {
                UsageHealth.Loading => L10n.T("updating"),
                UsageHealth.SignedOut => source == UsageSource.Claude
                    ? L10n.T("err_not_signed_in_hint")
                    : L10n.T("err_chatgpt_not_signed_in"),
                _ => L10n.T("err_usage_unavailable"),
            };
            AddUsageMessage(rows, explanation);
            return;
        }

        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in snapshot.Windows)
        {
            var key = $"{source}:{window.Key}";
            usedKeys.Add(key);
            if (!_usageRows.TryGetValue(key, out var row))
            {
                row = CreateUsageRow();
                _usageRows.Add(key, row);
            }

            var remaining = Math.Round(window.RemainingPercent);
            row.Label.Text = window.Label;
            row.Label.Foreground = ThemeManager.Brush(ThemeManager.LabelText);
            row.Value.Text = window.ResetsAt is DateTimeOffset reset
                ? $"{L10n.F("tray_usage_remaining", remaining)} · {FormatReset(reset)}"
                : L10n.F("tray_usage_remaining", remaining);
            row.Value.Foreground = ThemeManager.Brush(snapshot.Health is UsageHealth.Stale or UsageHealth.Unavailable
                ? ThemeManager.SubtleText
                : ThemeManager.ColorForAllowance(UsageSummary.Level(
                    window.RemainingPercent,
                    _settings.ApproachingBelowPercent,
                    _settings.LowBelowPercent)));
            RenderPaceBar(row, window, snapshot.Health is UsageHealth.Stale or UsageHealth.Unavailable);
            row.Root.ToolTip = snapshot.Health == UsageHealth.Stale && snapshot.LastSuccess is DateTimeOffset lastSuccess
                ? L10n.F("data_stale", lastSuccess.ToLocalTime().ToString("HH:mm"))
                : null;
            rows.Children.Add(row.Root);
        }

        RemoveUnusedUsageRows(source, usedKeys);
        if (snapshot.Health == UsageHealth.Stale && snapshot.LastSuccess is DateTimeOffset success)
            healthText.Text = L10n.F("data_stale", success.ToLocalTime().ToString("HH:mm"));
        else if (snapshot.Health == UsageHealth.Loading)
            healthText.Text = L10n.T("updating");
        else if (snapshot.Health == UsageHealth.Unavailable)
            healthText.Text = L10n.T("err_usage_unavailable");
    }

    void RenderPaceBar(UsageRow row, UsageWindow window, bool dimmed)
    {
        var now = DateTimeOffset.Now;
        var level = UsagePaceCalculator.Worst(
            window, now, _settings.ApproachingBelowPercent, _settings.LowBelowPercent);
        var brush = ThemeManager.Brush(dimmed
            ? ThemeManager.SubtleText
            : ThemeManager.ColorForAllowance(level));
        var remaining = Math.Clamp(window.RemainingPercent, 0, 100);
        row.FillColumn.Width = new GridLength(remaining, GridUnitType.Star);
        row.GapColumn.Width = new GridLength(100 - remaining, GridUnitType.Star);
        row.Fill.Background = brush;
        row.Track.Background = brush;
        row.Bar.Visibility = Visibility.Visible;

        if (UsagePaceCalculator.Evaluate(window, now) is UsagePace pace)
        {
            row.BeforeTick.Width = new GridLength(pace.TimeRemainingPercent, GridUnitType.Star);
            row.AfterTick.Width = new GridLength(100 - pace.TimeRemainingPercent, GridUnitType.Star);
            row.Tick.Background = ThemeManager.Brush(ThemeManager.LabelText);
            row.TickGrid.Visibility = Visibility.Visible;
        }
        else
        {
            row.TickGrid.Visibility = Visibility.Collapsed;
        }
    }

    static UsageRow CreateUsageRow()
    {
        var label = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var value = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);

        // Allowance bar with a tick at the share of the window's time still to run: fill past the
        // tick means ahead of pace, fill short of it means burning faster than the calendar.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var barGrid = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
        var fillColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var gapColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        barGrid.ColumnDefinitions.Add(fillColumn);
        barGrid.ColumnDefinitions.Add(gapColumn);
        var track = new Border { CornerRadius = new CornerRadius(1.5), Opacity = 0.25 };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(1.5) };
        var tickGrid = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var beforeTick = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var afterTick = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        tickGrid.ColumnDefinitions.Add(beforeTick);
        tickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tickGrid.ColumnDefinitions.Add(afterTick);
        var tick = new Border { Width = 1.5, Margin = new Thickness(-0.75, -2, -0.75, -2) };
        Grid.SetColumn(tick, 1);
        barGrid.Children.Add(track);
        barGrid.Children.Add(fill);
        tickGrid.Children.Add(tick);
        Grid.SetRow(barGrid, 1);
        Grid.SetRow(tickGrid, 1);
        Grid.SetColumnSpan(barGrid, 2);
        Grid.SetColumnSpan(tickGrid, 2);
        grid.Children.Add(barGrid);
        grid.Children.Add(tickGrid);
        var root = new Border { Padding = new Thickness(5, 4, 5, 4), CornerRadius = new CornerRadius(4) };
        root.Child = grid;
        return new UsageRow(root, label, value, barGrid, fillColumn, gapColumn, track, fill, tickGrid, beforeTick, afterTick, tick);
    }

    void RemoveUnusedUsageRows(UsageSource source, HashSet<string> usedKeys)
    {
        foreach (var key in _usageRows.Keys
                     .Where(key => key.StartsWith($"{source}:", StringComparison.Ordinal) && !usedKeys.Contains(key))
                     .ToArray())
        {
            _usageRows.Remove(key);
        }
    }

    static void AddUsageMessage(System.Windows.Controls.Panel rows, string message)
    {
        rows.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(5, 3, 5, 5),
            Foreground = ThemeManager.Brush(ThemeManager.SubtleText),
        });
    }

    string FormatReset(DateTimeOffset resetsAt)
    {
        var countdown = UsageParser.FormatCountdown(resetsAt);
        var remaining = resetsAt - DateTimeOffset.Now;
        return remaining.TotalHours > 24
            ? $"{countdown} · {resetsAt.ToLocalTime().ToString("ddd", System.Globalization.CultureInfo.CurrentCulture)}"
            : countdown;
    }

    void RenderTasks()
    {
        var panels = new[] { NeedsYouRows, WorkingRows, UnknownRows, RecentRows };
        foreach (var panel in panels) panel.Children.Clear();
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var counts = new int[4];
        foreach (var task in _tasks.Tasks)
        {
            var group = GroupFor(task.Status);
            if (group < 0) continue;
            counts[group]++;
            activeIds.Add(task.Id);
            if (!_taskRows.TryGetValue(task.Id, out var row))
            {
                row = new TaskRowView();
                row.DismissRequested += taskId => DismissTaskRequested?.Invoke(taskId);
                row.FocusRequested += task => FocusTaskRequested?.Invoke(task);
                _taskRows.Add(task.Id, row);
            }
            row.Update(task);
            panels[group].Children.Add(row);
        }

        foreach (var id in _taskRows.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _taskRows.Remove(id);

        NeedsYouHeading.Visibility = SetSection(NeedsYouHeading, counts[0]);
        WorkingHeading.Visibility = SetSection(WorkingHeading, counts[1]);
        UnknownHeading.Visibility = SetSection(UnknownHeading, counts[2]);
        RecentHeading.Visibility = SetSection(RecentHeading, counts[3]);
        TaskDivider.Visibility = _tasks.Tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTasks.Visibility = _tasks.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static Visibility SetSection(TextBlock heading, int count)
    {
        heading.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return heading.Visibility;
    }

    static int GroupFor(AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.NeedsAttention => 0,
        AgentTaskStatus.Working => 1,
        AgentTaskStatus.Unknown => 2,
        AgentTaskStatus.Complete or AgentTaskStatus.Failed => 3,
        _ => -1,
    };

    void OnCountdownTick(object? sender, EventArgs e)
    {
        RenderUsage(UsageSource.Codex, CodexUsageRows, CodexHealth);
        RenderUsage(UsageSource.Claude, ClaudeUsageRows, ClaudeHealth);
        RenderTasks();
    }

    void ConfigureAutoCollapseTimer()
    {
        if (_settings.PaneAutoCollapseSeconds <= 0)
        {
            _autoCollapseTimer.Stop();
            return;
        }

        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(_settings.PaneAutoCollapseSeconds);
        if (IsVisible)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Start();
        }
    }

    void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _countdownTimer.Start();
            ConfigureAutoCollapseTimer();
        }
        else
        {
            _countdownTimer.Stop();
            _autoCollapseTimer.Stop();
        }
    }

    void OnActivity(object sender, RoutedEventArgs e)
    {
        if (IsVisible && _settings.PaneAutoCollapseSeconds > 0)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Start();
        }
    }

    void OnAutoCollapseTick(object? sender, EventArgs e) => ClosePane();

    void OnDeactivated(object? sender, EventArgs e)
    {
        // Let a strip click toggle the pane before focus loss closes it.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ClosePane));
    }

    void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClosePane();
        e.Handled = true;
    }

    void ClosePane()
    {
        if (_closeRequested || !IsVisible) return;
        _closeRequested = true;
        Close();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        _autoCollapseTimer.Stop();
        L10n.Changed -= ApplyAppearance;
        ThemeManager.Changed -= ApplyAppearance;
    }

    sealed record UsageRow(
        Border Root, TextBlock Label, TextBlock Value,
        Grid Bar, ColumnDefinition FillColumn, ColumnDefinition GapColumn, Border Track, Border Fill,
        Grid TickGrid, ColumnDefinition BeforeTick, ColumnDefinition AfterTick, Border Tick);
}
