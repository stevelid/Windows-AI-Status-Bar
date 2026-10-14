using System.Globalization;
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
using StatusBar.Core.Projects;
using StatusBar.Core.Usage;
using WinForms = System.Windows.Forms;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>Shows provider quota windows and the merged task state above the status strip.</summary>
public partial class DetailsPaneWindow : Window
{
    const double PaneGapDip = 8;

    readonly Settings _settings;
    readonly TaskHistory? _history;
    readonly UsageRateHistory? _rates;
    readonly TaskActivityLog? _activity;
    ProjectOverviewDocument _projectDocument;
    bool _showProjects = true;
    Dictionary<string, double> _costs = new(StringComparer.Ordinal);
    double? _anchorBottom;
    double _workAreaTop;
    readonly Dictionary<string, UsageRow> _usageRows = new(StringComparer.Ordinal);
    readonly Dictionary<string, TaskRowView> _taskRows = new(StringComparer.Ordinal);
    readonly DispatcherTimer _countdownTimer;
    readonly DispatcherTimer _autoCollapseTimer;
    readonly DispatcherTimer _projectPeekOpenTimer;
    readonly DispatcherTimer _projectPeekCloseTimer;
    ProjectOverviewItem? _pendingProjectPeek;
    ProjectOverviewItem? _visibleProjectPeek;
    Border? _projectPeekTarget;
    bool _projectPeekPinned;
    bool _projectsDirty;
    bool _forceProjectRerank = true;
    DateTimeOffset _lastProjectRankAt;
    List<string> _projectOrder = [];
    bool _summariesUnavailable;
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _usage =
        new Dictionary<UsageSource, UsageSnapshot>();
    StatusBarState _tasks = StatusBarState.Empty;
    bool _closeRequested;

    /// <summary>Raised when the user dismisses an inferred attention or unknown task row.</summary>
    public event Action<string>? DismissTaskRequested;

    /// <summary>Raised when the user clears every finished task from the "recently completed" list.</summary>
    public event Action? ClearFinishedRequested;

    /// <summary>Raised when the user opens a finished task from the history.</summary>
    public event Action<AgentTask>? OpenHistoryTaskRequested;

    /// <summary>Raised when the user clicks a task row to focus its owning desktop app.</summary>
    public event Action<AgentTask>? FocusTaskRequested;

    /// <summary>Raised when a displayed update is opened so only its shown revision is acknowledged.</summary>
    public event Action<ProjectAcknowledgement, AgentTask>? ProjectUpdateOpenRequested;

    /// <summary>Raised when Steve moves a conversation to a project or clears its grouping.</summary>
    public event Action<string, string?>? ProjectAssignmentRequested;

    public event Action<string>? ProjectDeleteRequested;
    public event Action? ClearProjectOverviewRequested;
    public event Action? ProjectSummaryToggleRequested;

    /// <summary>Creates the pane and starts its display timers only while it is visible.</summary>
    public DetailsPaneWindow(Settings settings, TaskHistory? history = null, UsageRateHistory? rates = null, TaskActivityLog? activity = null, ProjectOverviewDocument? projectDocument = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _history = history;
        _rates = rates;
        _activity = activity;
        _projectDocument = projectDocument ?? ProjectOverviewDocumentRules.Clear();
        if (_history is not null) _history.Changed += OnHistoryChanged;
        InitializeComponent();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += OnCountdownTick;
        _autoCollapseTimer = new DispatcherTimer();
        _autoCollapseTimer.Tick += OnAutoCollapseTick;
        _projectPeekOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _projectPeekOpenTimer.Tick += (_, _) =>
        {
            _projectPeekOpenTimer.Stop();
            if (_pendingProjectPeek is not null && _projectPeekTarget is not null) OpenProjectPeek(_pendingProjectPeek, _projectPeekTarget);
        };
        _projectPeekCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _projectPeekCloseTimer.Tick += (_, _) =>
        {
            _projectPeekCloseTimer.Stop();
            if (!_projectPeekPinned) ProjectPeekPopup.IsOpen = false;
            if (_projectsDirty && !ProjectOverviewRows.IsMouseOver) RenderProjects();
        };
        IsVisibleChanged += OnVisibilityChanged;
        SizeChanged += OnPaneSizeChanged;
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
        RenderRates();
        RenderTasks();
        RenderProjects();
    }

    /// <summary>Shows the pane above its owner and clamps it to that monitor's work area.</summary>
    public void ShowAbove(StatusStripWindow strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        Owner = strip;
        _closeRequested = false;
        _forceProjectRerank = true;
        Show();
        UpdateLayout();
        RenderProjects();

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
        // Remember where the pane's bottom edge sits above the strip, so it can grow upwards later.
        _anchorBottom = placed.Y + ActualHeight;
        _workAreaTop = workAreaDip.Y;
        Activate();
        Focus();
    }

    // The pane hangs off the strip: when its content grows (a task arrives, history expands) it must
    // extend upwards, otherwise it slides down over the strip and the taskbar.
    void OnPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchorBottom is not double bottom || !e.HeightChanged) return;
        Top = Math.Max(_workAreaTop, bottom - e.NewSize.Height);
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
        Resources["PaneTextBrush"] = ThemeManager.Brush(ThemeManager.TitleText);
        Resources["PaneMutedBrush"] = ThemeManager.Brush(ThemeManager.StatusText);
        Resources["PaneSurfaceBrush"] = ThemeManager.Brush(surface);
        Resources["PaneHoverBrush"] = ThemeManager.Brush(ThemeManager.IsLight
            ? Color.FromRgb(0xE3, 0xE4, 0xEB) : Color.FromRgb(0x30, 0x30, 0x3C));
        PaneShadow.Color = ThemeManager.IsLight ? Color.FromRgb(0x42, 0x45, 0x52) : Colors.Black;
        PaneShadow.Opacity = ThemeManager.IsLight ? 0.24 : 0.42;
        TaskDivider.Background = ThemeManager.Brush(ThemeManager.IsLight
            ? Color.FromRgb(0xD8, 0xD9, 0xE0)
            : Color.FromRgb(0x4B, 0x4C, 0x56));
        Resources["PaneBorderBrush"] = TaskDivider.Background;
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
                     WorkingHeading, UnknownHeading, RecentHeading, HistoryHeading,
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
        var linkBrush = ThemeManager.Brush(ThemeManager.SubtleText);
        RecentClear.Text = L10n.T("pane_recent_clear");
        RecentClear.Foreground = linkBrush;
        HistoryClear.Text = L10n.T("pane_history_clear");
        HistoryClear.Foreground = linkBrush;
        EmptyTasks.Text = L10n.T("pane_empty_tasks");
        ProjectsTab.Content = L10n.T("pane_projects");
        ConversationsTab.Content = L10n.T("pane_conversations");
        ProjectOptions.ToolTip = L10n.T("pane_project_options");
        ClearProjectsMenuItem.Header = L10n.T("pane_project_clear");
        ClearProjectsMenuItem.ToolTip = L10n.T("pane_project_clear_hint");
        ProjectPeekPin.Content = L10n.T(_projectPeekPinned ? "pane_project_summary_unpin" : "pane_project_summary_pin");
        ProjectPeekTitle.Foreground = ThemeManager.Brush(ThemeManager.TitleText);
        ProjectPeekSummary.Foreground = ThemeManager.Brush(ThemeManager.LabelText);
        UpdateProjectSummaryStatus(_settings.ProjectSummariesEnabled, _summariesUnavailable);
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
            var runway = snapshot.RunwayFor(window);
            RenderPaceBar(row, window, runway, snapshot.Health is UsageHealth.Stale or UsageHealth.Unavailable);
            var tooltipLines = new List<string>();
            if (snapshot.Health == UsageHealth.Stale && snapshot.LastSuccess is DateTimeOffset lastSuccess)
                tooltipLines.Add(L10n.F("data_stale", lastSuccess.ToLocalTime().ToString("HH:mm")));
            if (RunwayText(runway) is string projection) tooltipLines.Add(projection);
            row.Root.ToolTip = tooltipLines.Count > 0 ? string.Join(Environment.NewLine, tooltipLines) : null;
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

    // Bar colour: the more severe of the absolute level and the rate-based projection (the strip's
    // rule), so the pane explains why the strip changed colour. The tick still marks time left.
    void RenderPaceBar(UsageRow row, UsageWindow window, UsageRunway runway, bool dimmed)
    {
        var now = DateTimeOffset.Now;
        var absolute = UsageSummary.Level(
            window.RemainingPercent, _settings.ApproachingBelowPercent, _settings.LowBelowPercent);
        var level = runway.Level > absolute ? runway.Level : absolute;
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

    // "At the current rate: lasts until reset" or "runs out in ~2h 10m (resets in 3h 12m)".
    static string? RunwayText(UsageRunway runway)
    {
        if (runway.TimeToReset is not TimeSpan reset) return null;
        if (runway.TimeToEmpty is not TimeSpan empty)
            return runway.Level == AllowanceLevel.Normal ? null : L10n.T("pane_runway_unknown");
        return empty < reset
            ? L10n.F("pane_runway_runs_out", FormatSpan(empty), FormatSpan(reset))
            : L10n.T("pane_runway_lasts");
    }

    static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
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

    // A compact burn-rate strip above each provider's session (five-hour) window. It is hidden until the
    // provider has a session window, and shows "collecting usage" until readings arrive.
    void RenderRates()
    {
        RenderRate(UsageSource.Codex, CodexRate);
        RenderRate(UsageSource.Claude, ClaudeRate);
    }

    void RenderRate(UsageSource source, RateStrip strip)
    {
        strip.Visibility = Visibility.Collapsed;
        if (_rates is null || !_usage.TryGetValue(source, out var snapshot)) return;
        if (snapshot.Health is UsageHealth.SignedOut or UsageHealth.Loading) return;
        var window = UsageSummary.Compact(snapshot.Windows);
        if (window is not { Length: { } length } || length > UsageRateHistory.MaximumWindowLength) return;

        var series = _rates.Series(source, window.Key);
        var stale = snapshot.Health is UsageHealth.Stale or UsageHealth.Unavailable;
        var level = UsageSummary.Level(window.RemainingPercent, _settings.ApproachingBelowPercent, _settings.LowBelowPercent);
        strip.Update(series, stale ? ThemeManager.SubtleText : ThemeManager.ColorForAllowance(level));
        strip.Describe = point => DescribeSlice(source, point);
        strip.Visibility = Visibility.Visible;
    }

    // What the hover on the strip says: the slice's time and rate, and which conversations were working then.
    string DescribeSlice(UsageSource source, RatePoint point)
    {
        var from = point.At - UsageRateHistory.Bucket / 2;
        var to = point.At + UsageRateHistory.Bucket / 2;
        var time = $"{from.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}–{to.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";
        if (point.PerHour is not double rate) return time + " · " + L10n.T("pane_rate_hover_nodata");

        var lines = new List<string> { time + " · " + L10n.F("pane_rate_hover_rate", Math.Round(rate)) };
        var running = _activity?.RunningBetween(ProviderOf(source), from, to) ?? Array.Empty<ActivityInterval>();
        if (running.Count == 0)
        {
            // A stretch from before the app was running says nothing about what was working, so it must not
            // claim that nothing was.
            var watched = _activity is null || _activity.WasWatching(from, to);
            lines.Add(L10n.T(!watched ? "pane_rate_hover_not_tracked" : rate >= 1 ? "pane_rate_hover_untracked" : "pane_rate_hover_idle"));
        }
        else
        {
            lines.AddRange(running.Take(3).Select(interval => "● " + TitleOf(interval)));
            if (running.Count > 3) lines.Add(L10n.F("pane_rate_hover_more", running.Count - 3));
        }

        return string.Join("\n", lines);
    }

    // Intervals loaded from the activity file carry no title (titles are never saved), so look the
    // conversation up in the live task list and the history, then fall back to a short id.
    string TitleOf(ActivityInterval interval)
    {
        if (!string.IsNullOrWhiteSpace(interval.Title)) return interval.Title;
        var task = _tasks.Tasks.FirstOrDefault(candidate => string.Equals(candidate.Id, interval.TaskId, StringComparison.Ordinal));
        if (task is not null && !string.IsNullOrWhiteSpace(task.Title)) return task.Title;
        var entry = _history?.Entries.FirstOrDefault(candidate => string.Equals(candidate.TaskId, interval.TaskId, StringComparison.Ordinal));
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Title)) return entry.Title;

        var id = interval.TaskId;
        var colon = id.IndexOf(':');
        if (colon >= 0) id = id[(colon + 1)..];
        var shortId = id.Length > 8 ? id[^8..] : id;
        return L10n.F(interval.Provider == AgentProvider.Codex ? "pane_activity_codex_conversation" : "pane_activity_claude_conversation", shortId);
    }

    static AgentProvider ProviderOf(UsageSource source) => source == UsageSource.Codex ? AgentProvider.Codex : AgentProvider.Claude;

    // Estimated points of each provider's five-hour window used by each conversation over the last three hours.
    Dictionary<string, double> ComputeCosts()
    {
        var costs = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_rates is null || _activity is null) return costs;
        foreach (var source in new[] { UsageSource.Codex, UsageSource.Claude })
        {
            if (!_usage.TryGetValue(source, out var snapshot)) continue;
            var window = UsageSummary.Compact(snapshot.Windows);
            if (window is not { Length: { } length } || length > UsageRateHistory.MaximumWindowLength) continue;
            var attribution = UsageAttributionCalculator.Compute(
                _rates.Readings(source, window.Key),
                _activity.Intervals(ProviderOf(source)),
                DateTimeOffset.UtcNow,
                TaskActivityLog.Retention);
            foreach (var (taskId, points) in attribution.PointsByTask) costs[taskId] = points;
        }

        return costs;
    }

    void RenderTasks()
    {
        _costs = ComputeCosts();
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
            row.Update(task, _costs.GetValueOrDefault(task.Id));
            panels[group].Children.Add(row);
        }

        foreach (var id in _taskRows.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _taskRows.Remove(id);

        NeedsYouHeading.Visibility = SetSection(NeedsYouHeading, counts[0]);
        WorkingHeading.Visibility = SetSection(WorkingHeading, counts[1]);
        UnknownHeading.Visibility = SetSection(UnknownHeading, counts[2]);
        RecentHeader.Visibility = counts[3] > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskDivider.Visibility = _tasks.Tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderHistory();
        EmptyTasks.Visibility = _tasks.Tasks.Count == 0 && HistoryHeader.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void OnProjectsTabClick(object sender, RoutedEventArgs e) => SetProjectView(true);

    void OnConversationsTabClick(object sender, RoutedEventArgs e) => SetProjectView(false);

    void SetProjectView(bool showProjects)
    {
        _showProjects = showProjects;
        ProjectOverviewRows.Visibility = showProjects ? Visibility.Visible : Visibility.Collapsed;
        ConversationTasks.Visibility = showProjects ? Visibility.Collapsed : Visibility.Visible;
        ProjectsTab.FontWeight = showProjects ? FontWeights.Bold : FontWeights.Normal;
        ConversationsTab.FontWeight = showProjects ? FontWeights.Normal : FontWeights.Bold;
        var selected = ThemeManager.Brush(ThemeManager.ColorForAllowance(AllowanceLevel.Normal));
        var muted = ThemeManager.Brush(ThemeManager.StatusText);
        ProjectsTab.Foreground = showProjects ? selected : muted;
        ConversationsTab.Foreground = showProjects ? muted : selected;
        ProjectsTab.BorderBrush = showProjects ? selected : System.Windows.Media.Brushes.Transparent;
        ConversationsTab.BorderBrush = showProjects ? System.Windows.Media.Brushes.Transparent : selected;
        ProjectsTab.BorderThickness = ConversationsTab.BorderThickness = new Thickness(0, 0, 0, 1);
        ProjectOptions.Visibility = showProjects ? Visibility.Visible : Visibility.Collapsed;
        SummaryStatus.Visibility = showProjects && (_summariesUnavailable || !_settings.ProjectSummariesEnabled)
            ? Visibility.Visible : Visibility.Collapsed;
        if (showProjects) TaskDivider.Visibility = Visibility.Visible;
    }

    void OnProjectOptionsClick(object sender, RoutedEventArgs e)
    {
        ConfigureAutoCollapseTimer();
        ProjectOptions.ContextMenu.PlacementTarget = ProjectOptions;
        ProjectOptions.ContextMenu.IsOpen = true;
    }

    void OnSummaryToggleClick(object sender, RoutedEventArgs e) => ProjectSummaryToggleRequested?.Invoke();

    void OnClearProjectsClick(object sender, RoutedEventArgs e) => ClearProjectOverviewRequested?.Invoke();

    public void UpdateProjectDocument(ProjectOverviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _projectDocument = document;
        RenderProjects();
    }

    public void UpdateProjectSummaryStatus(bool enabled, bool unavailable = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProjectSummaryStatus(enabled, unavailable));
            return;
        }
        _settings.ProjectSummariesEnabled = enabled;
        _summariesUnavailable = unavailable;
        SummaryToggleMenuItem.Header = L10n.T(enabled ? "pane_project_pause_summaries" : "pane_project_resume_summaries");
        SummaryStatus.Text = L10n.T(unavailable ? "pane_project_summary_retrying" : "pane_project_summary_paused_short");
        SummaryStatus.ToolTip = L10n.T(unavailable ? "pane_project_summary_unavailable" : "pane_project_summary_paused");
        SummaryStatus.Visibility = _showProjects && (unavailable || !enabled) ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderProjects()
    {
        if (ProjectPeekPopup.IsOpen || ProjectOverviewRows.IsMouseOver)
        {
            _projectsDirty = true;
            return;
        }
        ProjectOverviewRows.Children.Clear();
        _projectsDirty = false;
        var state = ProjectOverviewBuilder.Build(_projectDocument.Projects, _projectDocument.Memberships,
            _tasks.Tasks, _projectDocument.Updates, _projectDocument.Acknowledgements, _projectDocument.ThreadReferences);
        if (_forceProjectRerank || DateTimeOffset.UtcNow - _lastProjectRankAt >= TimeSpan.FromMinutes(15))
        {
            _projectOrder = state.Projects.Select(item => item.Project.Id).ToList();
            _lastProjectRankAt = DateTimeOffset.UtcNow;
            _forceProjectRerank = false;
        }
        var ranks = _projectOrder.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
        state = state with { Projects = state.Projects.OrderBy(item => ranks.GetValueOrDefault(item.Project.Id, int.MaxValue)).ToArray() };
        foreach (var item in state.Projects)
        {
            var content = new StackPanel { Margin = new Thickness(7, 4, 7, 4) };
            var activeRow = item.HasWorkingConversation || item.HasUnresolvedInput || item.UnseenUpdateCount > 0;
            var headingGrid = new Grid();
            headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headingGrid.Children.Add(new TextBlock
            {
                Text = item.HasUnresolvedInput ? "!" : item.HasWorkingConversation ? "●" : item.UnseenUpdateCount > 0 ? "•" : "✓",
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
                Foreground = ThemeManager.Brush(item.HasUnresolvedInput ? ThemeManager.SoftColorForAllowance(AllowanceLevel.Approaching)
                    : activeRow ? ThemeManager.ColorForAllowance(AllowanceLevel.Normal) : ThemeManager.SubtleText),
            });
            var heading = new TextBlock
            {
                Text = item.Project.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Project.Name, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeManager.Brush(ThemeManager.TitleText),
                Focusable = true,
            };
            heading.ContextMenu = new System.Windows.Controls.ContextMenu();
            var deleteProject = new System.Windows.Controls.MenuItem { Header = L10n.T("pane_project_delete") };
            deleteProject.Click += (_, _) => ProjectDeleteRequested?.Invoke(item.Project.Id);
            heading.ContextMenu.Items.Add(deleteProject);
            heading.Cursor = System.Windows.Input.Cursors.Hand;
            heading.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                _projectPeekPinned = true;
                OpenProjectPeek(item, (Border)heading.Tag!);
            };
            heading.KeyDown += (_, args) =>
            {
                if (args.Key is not (Key.Enter or Key.Space)) return;
                _projectPeekPinned = true;
                OpenProjectPeek(item, (Border)heading.Tag!);
                args.Handled = true;
            };
            Grid.SetColumn(heading, 1);
            headingGrid.Children.Add(heading);
            var statusText = item.HasUnresolvedInput ? L10n.T("pane_needs_you") : item.HasWorkingConversation
                ? L10n.T("pane_working") : item.UnseenUpdateCount > 0 ? L10n.F("pane_project_new_count", item.UnseenUpdateCount)
                : item.Conversations.Count == 1 ? L10n.T("pane_project_one_thread")
                : L10n.F("pane_project_thread_count", item.Conversations.Count);
            var status = new TextBlock { Text = statusText, FontSize = 10, Margin = new Thickness(7, 0, 0, 0),
                Foreground = ThemeManager.Brush(ThemeManager.StatusText), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(status, 2);
            headingGrid.Children.Add(status);
            content.Children.Add(headingGrid);

            var byThread = item.Conversations.ToDictionary(conversation => conversation.Task.Id, conversation => conversation.Task, StringComparer.Ordinal);
            var visibleUpdates = item.Updates.GroupBy(update => update.ThreadId, StringComparer.Ordinal)
                .Select(group => group.First()).Take(ProjectOverviewBuilder.VisibleUpdatesPerProject).ToArray();
            if (activeRow)
                foreach (var update in visibleUpdates)
                    content.Children.Add(CreateProjectUpdateButton(update, byThread));
            var overflowCount = item.Updates.Count - visibleUpdates.Length;
            if (overflowCount > 0 && activeRow)
            {
                var toggle = new System.Windows.Controls.Button { Content = L10n.F("pane_project_more", overflowCount),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 1), FontSize = 10,
                    Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                    Foreground = ThemeManager.Brush(ThemeManager.SubtleText) };
                toggle.Click += (_, _) =>
                {
                    _projectPeekPinned = true;
                    OpenProjectPeek(item, (Border)heading.Tag!);
                };
                content.Children.Add(toggle);
            }
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(5),
                Background = activeRow ? ThemeManager.Brush(ThemeManager.IsLight ? Color.FromRgb(0xEA, 0xEA, 0xEF) : Color.FromRgb(0x2A, 0x2A, 0x34))
                    : System.Windows.Media.Brushes.Transparent,
                Child = content,
            };
            heading.Tag = card;
            card.MouseEnter += (_, _) => ScheduleProjectPeek(item, card);
            card.MouseLeave += (_, _) => ScheduleProjectPeekClose();
            ProjectOverviewRows.Children.Add(card);
        }

        if (state.UngroupedConversations.Count > 0)
        {
            ProjectOverviewRows.Children.Add(new TextBlock { Text = L10n.T("pane_project_ungrouped"), FontSize = 11,
                FontWeight = FontWeights.Bold, Margin = new Thickness(0, 7, 0, 3), Foreground = ThemeManager.Brush(ThemeManager.LabelText) });
            foreach (var task in state.UngroupedConversations)
            {
                var row = new TaskRowView();
                row.Update(task, _costs.TryGetValue(task.Id, out var cost) ? cost : null);
                row.ContextMenu = CreateAssignmentMenu(task.Id);
                row.FocusRequested += selected => FocusTaskRequested?.Invoke(selected);
                row.DismissRequested += id => DismissTaskRequested?.Invoke(id);
                ProjectOverviewRows.Children.Add(row);
            }
        }

        if (ProjectOverviewRows.Children.Count == 0)
            ProjectOverviewRows.Children.Add(new TextBlock { Text = L10n.T("pane_projects_empty"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 8, 2, 8), Foreground = ThemeManager.Brush(ThemeManager.SubtleText) });
        SetProjectView(_showProjects);
    }

    System.Windows.Controls.ContextMenu CreateAssignmentMenu(string threadId)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var move = new System.Windows.Controls.MenuItem { Header = L10n.T("pane_project_move_to") };
        foreach (var project in _projectDocument.Projects)
        {
            var choice = new System.Windows.Controls.MenuItem { Header = project.Name };
            choice.Click += (_, _) => ProjectAssignmentRequested?.Invoke(threadId, project.Id);
            move.Items.Add(choice);
        }
        menu.Items.Add(move);
        var ungroup = new System.Windows.Controls.MenuItem { Header = L10n.T("pane_project_ungroup") };
        ungroup.Click += (_, _) => ProjectAssignmentRequested?.Invoke(threadId, null);
        menu.Items.Add(ungroup);
        return menu;
    }

    System.Windows.Controls.Button CreateProjectUpdateButton(ProjectUpdate update, IReadOnlyDictionary<string, AgentTask> taskById, string? label = null)
    {
        var provider = taskById.TryGetValue(update.ThreadId, out var source) ? source.Provider + " · " : string.Empty;
        var text = label ?? ("↗ " + provider + update.LatestAction);
        var row = new System.Windows.Controls.Button { Content = new TextBlock { Text = text, FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text }, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(0, 2, 0, 1), Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = ThemeManager.Brush(ThemeManager.LabelText),
            ToolTip = text };
        row.Click += (_, _) =>
        {
            if (taskById.TryGetValue(update.ThreadId, out var task))
                ProjectUpdateOpenRequested?.Invoke(new ProjectAcknowledgement(update.Id, update.Revision), task);
        };
        return row;
    }

    void ScheduleProjectPeek(ProjectOverviewItem item, Border target)
    {
        _projectPeekCloseTimer.Stop();
        if (_projectPeekPinned && _visibleProjectPeek?.Project.Id != item.Project.Id) return;
        _pendingProjectPeek = item;
        _projectPeekTarget = target;
        _projectPeekOpenTimer.Stop();
        _projectPeekOpenTimer.Start();
    }

    void ScheduleProjectPeekClose()
    {
        _projectPeekOpenTimer.Stop();
        if (!_projectPeekPinned && (ProjectPeekPopup.IsOpen || _projectsDirty))
        {
            _projectPeekCloseTimer.Stop();
            _projectPeekCloseTimer.Start();
        }
    }

    void OpenProjectPeek(ProjectOverviewItem item, Border target)
    {
        _visibleProjectPeek = item;
        _pendingProjectPeek = item;
        ProjectPeekTitle.Text = item.Project.Name;
        ProjectPeekPin.Content = L10n.T(_projectPeekPinned ? "pane_project_summary_unpin" : "pane_project_summary_pin");
        ProjectPeekSummary.Text = item.Project.CurrentSummary ?? L10n.T("pane_project_summary_pending");
        ProjectPeekRows.Children.Clear();
        var updatesByThread = item.Updates.GroupBy(update => update.ThreadId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var conversation in item.Conversations)
        {
            var task = conversation.Task;
            var status = task.Status switch
            {
                AgentTaskStatus.Working => L10n.T("pane_working"),
                AgentTaskStatus.NeedsAttention => L10n.T("pane_needs_you"),
                AgentTaskStatus.Complete => L10n.T("pane_recent"),
                _ => L10n.T("pane_unknown"),
            };
            var conversationButton = new System.Windows.Controls.Button
            {
                Content = new TextBlock { Text = $"{task.Provider} · {task.Title}", FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{task.Title} · {status}" },
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(3, 4, 3, 4),
            };
            conversationButton.ContextMenu = CreateAssignmentMenu(task.Id);
            conversationButton.Click += (_, _) => FocusTaskRequested?.Invoke(task);
            ProjectPeekRows.Children.Add(conversationButton);
            if (!updatesByThread.TryGetValue(task.Id, out var updates)) continue;
            foreach (var update in updates)
            {
                var action = CreateProjectUpdateButton(update, new Dictionary<string, AgentTask>(StringComparer.Ordinal) { [task.Id] = task });
                ProjectPeekRows.Children.Add(action);
                foreach (var progress in update.Progress)
                    ProjectPeekRows.Children.Add(CreateProjectUpdateButton(update,
                        new Dictionary<string, AgentTask>(StringComparer.Ordinal) { [task.Id] = task }, "• " + progress));
            }
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = WinForms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var rightFits = Left + ActualWidth + 320 <= screen.WorkingArea.Right / dpi.DpiScaleX;
        ProjectPeekPopup.PlacementTarget = target;
        ProjectPeekPopup.Placement = rightFits ? System.Windows.Controls.Primitives.PlacementMode.Right : System.Windows.Controls.Primitives.PlacementMode.Left;
        ProjectPeekPopup.IsOpen = true;
        _projectPeekCloseTimer.Stop();
    }

    void OnProjectPeekEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _projectPeekCloseTimer.Stop();
        ConfigureAutoCollapseTimer();
    }

    void OnProjectPeekLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleProjectPeekClose();

    void OnProjectPeekPinClick(object sender, RoutedEventArgs e)
    {
        _projectPeekPinned = !_projectPeekPinned;
        ProjectPeekPin.Content = L10n.T(_projectPeekPinned ? "pane_project_summary_unpin" : "pane_project_summary_pin");
    }

    void OnProjectPeekCloseClick(object sender, RoutedEventArgs e)
    {
        _projectPeekPinned = false;
        _projectPeekOpenTimer.Stop();
        _projectPeekCloseTimer.Stop();
        ProjectPeekPopup.IsOpen = false;
        _visibleProjectPeek = null;
        if (_projectsDirty && !ProjectOverviewRows.IsMouseOver) RenderProjects();
    }

    // History is a collapsed "HISTORY ▸ n" row under the task list; it expands in place. Tasks that
    // are still listed under "recently completed" are left out so nothing appears twice.
    void RenderHistory()
    {
        HistoryRows.Children.Clear();
        var listed = _tasks.Tasks
            .Where(task => task.Status is AgentTaskStatus.Complete or AgentTaskStatus.Failed)
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var entries = (_history?.Entries ?? Array.Empty<HistoryEntry>())
            .Where(entry => !listed.Contains(entry.TaskId))
            .ToArray();
        if (entries.Length == 0)
        {
            HistoryHeader.Visibility = Visibility.Collapsed;
            HistoryRows.Visibility = Visibility.Collapsed;
            return;
        }

        var expanded = _settings.HistoryExpanded;
        HistoryHeader.Visibility = Visibility.Visible;
        HistoryHeading.Text = $"{(expanded ? "▾" : "▸")} {L10n.T("pane_history")} · {entries.Length}";
        HistoryClear.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        HistoryRows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (!expanded) return;

        var today = DateTime.Now.Date;
        string? lastGroup = null;
        foreach (var entry in entries)
        {
            var day = entry.FinishedAt.ToLocalTime().Date;
            var group = day == today ? "pane_history_today" : day == today.AddDays(-1) ? "pane_history_yesterday" : "pane_history_earlier";
            if (group != lastGroup)
            {
                HistoryRows.Children.Add(new TextBlock
                {
                    Text = L10n.T(group),
                    FontSize = 10,
                    Margin = new Thickness(2, lastGroup is null ? 2 : 8, 0, 3),
                    Foreground = ThemeManager.Brush(ThemeManager.SubtleText),
                });
                lastGroup = group;
            }

            var row = new HistoryRowView(entry, _costs.TryGetValue(entry.TaskId, out var cost) ? cost : null);
            row.OpenRequested += item => OpenHistoryTaskRequested?.Invoke(item.ToTask());
            row.RemoveRequested += taskId => _history?.Remove(taskId);
            HistoryRows.Children.Add(row);
        }
    }

    void OnHistoryChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (IsVisible && !_closeRequested) RenderHistory();
    });

    void OnHistoryToggleClick(object sender, MouseButtonEventArgs e)
    {
        _settings.HistoryExpanded = !_settings.HistoryExpanded;
        _settings.Save();
        RenderHistory();
        e.Handled = true;
    }

    void OnClearHistoryClick(object sender, MouseButtonEventArgs e)
    {
        _history?.Clear();
        e.Handled = true;
    }

    void OnClearFinishedClick(object sender, MouseButtonEventArgs e)
    {
        ClearFinishedRequested?.Invoke();
        e.Handled = true;
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
        RenderRates();
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
        if (ProjectOptions.ContextMenu.IsOpen) return;
        if (ProjectPeekPopup.IsOpen && _projectPeekPinned) return;
        // Let a strip click toggle the pane before focus loss closes it.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ClosePane));
    }

    void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ProjectPeekPopup.IsOpen)
        {
            _projectPeekPinned = false;
            ProjectPeekPopup.IsOpen = false;
            _visibleProjectPeek = null;
        }
        else ClosePane();
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
        _projectPeekOpenTimer.Stop();
        _projectPeekCloseTimer.Stop();
        ProjectPeekPopup.IsOpen = false;
        _countdownTimer.Stop();
        _autoCollapseTimer.Stop();
        L10n.Changed -= ApplyAppearance;
        ThemeManager.Changed -= ApplyAppearance;
        if (_history is not null) _history.Changed -= OnHistoryChanged;
    }

    sealed record UsageRow(
        Border Root, TextBlock Label, TextBlock Value,
        Grid Bar, ColumnDefinition FillColumn, ColumnDefinition GapColumn, Border Track, Border Fill,
        Grid TickGrid, ColumnDefinition BeforeTick, ColumnDefinition AfterTick, Border Tick);
}
