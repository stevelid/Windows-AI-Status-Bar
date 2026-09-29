using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ClaudeUsageWidget;

/// <summary>The compact, docked status strip above the Windows taskbar.</summary>
public partial class StatusStripWindow : Window
{
    readonly Settings _settings;
    readonly SolidColorBrush _attentionBackgroundBrush = new(Colors.Transparent);
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _usage = new Dictionary<UsageSource, UsageSnapshot>();
    StatusBarState _tasks = StatusBarState.Empty;
    string? _transientMessage;
    int _lastAttentionCount;
    Point? _pressPoint;
    bool _dragged;

    /// <summary>Raised when the user clicks the strip to toggle the details pane.</summary>
    public event Action? TogglePaneRequested;

    /// <summary>Raised when the user right-clicks the strip.</summary>
    public event Action? ContextMenuRequested;

    /// <summary>Raised around a user drag so docking can save the new position.</summary>
    public event Action? DragStarted;
    public event Action? DragCompleted;

    /// <summary>Creates the strip from current settings and applies its initial appearance.</summary>
    public StatusStripWindow(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        InitializeComponent();
        AttentionPill.Background = _attentionBackgroundBrush;
        ApplyScale();
        ApplyAppearance();
        L10n.Changed += ApplyAppearance;
        ThemeManager.Changed += ApplyAppearance;
        Closed += OnClosed;
    }

    /// <summary>Replaces the provider usage snapshots and merged task state shown in the strip.</summary>
    public void UpdateState(
        IReadOnlyDictionary<UsageSource, UsageSnapshot> usage,
        StatusBarState tasks)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(tasks);
        _usage = usage;
        _tasks = tasks;
        _transientMessage = null;
        RenderState();
    }

    /// <summary>Updates display scale while keeping the strip at its fixed base height.</summary>
    public void ApplyScale()
    {
        var scale = Math.Clamp(_settings.UiScale, 0.7, 2.5);
        Height = 30 * scale;
        RootBorder.LayoutTransform = new ScaleTransform(scale, scale);
    }

    /// <summary>Applies theme colors, transparency and localized labels.</summary>
    public void ApplyAppearance()
    {
        var alpha = (byte)Math.Clamp(
            (int)Math.Round(255 * (100 - _settings.BgTransparency) / 100.0), 2, 255);
        var surface = ThemeManager.IsLight
            ? Color.FromRgb(0xF3, 0xF3, 0xF7)
            : Color.FromRgb(0x1B, 0x1B, 0x24);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
        RootBorder.BorderBrush = ThemeManager.IsLight
            ? ThemeManager.Brush(Color.FromRgb(0xB8, 0xB9, 0xC3))
            : ThemeManager.Brush(Color.FromRgb(0x5A, 0x5B, 0x67));
        RootShadow.Color = ThemeManager.IsLight ? Color.FromRgb(0x42, 0x45, 0x52) : Colors.Black;
        RootShadow.Opacity = ThemeManager.IsLight ? 0.24 : 0.42;
        RenderState();
    }

    /// <summary>Hides the strip's summary tooltip while the pane shows the same information.</summary>
    public void SetPaneOpen(bool open)
    {
        // ToolTipService.IsEnabled is not inherited, so each element with its own tooltip is set.
        foreach (DependencyObject element in new DependencyObject[] { this, CodexText, ClaudeText, WorkingText, DoneText, AttentionPill })
            ToolTipService.SetIsEnabled(element, !open);
    }

    /// <summary>Shows a short message as the strip's tooltip without replacing quota values.</summary>
    public void ShowLoading(string message) => SetTransientMessage(message);

    /// <summary>Shows a short error as the strip's tooltip without replacing quota values.</summary>
    public void ShowError(string message) => SetTransientMessage(message);

    /// <summary>Shows a short notice as the strip's tooltip without replacing quota values.</summary>
    public void ShowNotice(string message) => SetTransientMessage(message);

    /// <summary>Shows updater progress in the strip's tooltip.</summary>
    public void ShowUpdateProgress(string message, double? percent, bool canCancel)
    {
        var progress = percent is double value ? $" {Math.Round(Math.Clamp(value, 0, 100))}%" : "";
        SetTransientMessage(message + progress);
    }

    /// <summary>Clears a transient status message and restores the normal tooltip.</summary>
    public void HideUpdateProgress()
    {
        _transientMessage = null;
        RenderState();
    }

    void SetTransientMessage(string message)
    {
        _transientMessage = message;
        ToolTip = message;
    }

    void RenderState()
    {
        if (CodexText is null) return;
        RenderProvider(UsageSource.Codex, CodexText,
            new PaceBar(CodexBar, CodexBarFillColumn, CodexBarGapColumn, CodexBarTrack, CodexBarFill));
        RenderProvider(UsageSource.Claude, ClaudeText,
            new PaceBar(ClaudeBar, ClaudeBarFillColumn, ClaudeBarGapColumn, ClaudeBarTrack, ClaudeBarFill));

        WorkingText.Text = $"● {_tasks.WorkingCount}";
        WorkingText.Foreground = ThemeManager.Brush(ThemeManager.LabelText);
        WorkingText.ToolTip = L10n.F("strip_working_tooltip", _tasks.WorkingCount);
        RenderDone();

        if (_tasks.AttentionCount > 0)
        {
            var label = string.IsNullOrWhiteSpace(_settings.AttentionLabel)
                ? "STEVE"
                : _settings.AttentionLabel.Trim();
            if (label.Length > 12) label = label[..12];
            AttentionText.Text = $"⚠ {label} {_tasks.AttentionCount}";
            var attentionColor = ThemeManager.IsLight
                ? Color.FromRgb(0xF3, 0xD3, 0x94)
                : Color.FromRgb(0x61, 0x49, 0x25);
            if (_tasks.AttentionCount > _lastAttentionCount)
                AnimateAttention(attentionColor);
            else
            {
                _attentionBackgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    null);
                _attentionBackgroundBrush.Color = attentionColor;
            }
            AttentionText.Foreground = ThemeManager.Brush(ThemeManager.IsLight
                ? Color.FromRgb(0x62, 0x42, 0x0D)
                : Color.FromRgb(0xFF, 0xDC, 0x9A));
            AttentionPill.ToolTip = L10n.F("strip_attention_tooltip", _tasks.AttentionCount);
            AttentionPill.Visibility = Visibility.Visible;
        }
        else
        {
            _attentionBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _attentionBackgroundBrush.Color = Colors.Transparent;
            AttentionPill.Visibility = Visibility.Collapsed;
        }

        _lastAttentionCount = _tasks.AttentionCount;
        if (_transientMessage is null)
        {
            var tip = $"{CodexText.ToolTip}\n{ClaudeText.ToolTip}\n{WorkingText.ToolTip}";
            if (DoneText.Visibility == Visibility.Visible) tip += $"\n{DoneText.ToolTip}";
            ToolTip = tip;
        }
    }

    // Recently finished turns: "✓ 2", plus "✕ 1" when a turn failed. The count falls as tasks
    // leave the "recently completed" window (Settings), so it needs no separate dismissal.
    void RenderDone()
    {
        var done = _tasks.DoneCount;
        var failed = _tasks.FailedCount;
        if (done == 0 && failed == 0)
        {
            DoneText.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (done > 0) parts.Add($"✓ {done}");
        if (failed > 0) parts.Add($"✕ {failed}");
        DoneText.Text = string.Join("  ", parts);
        DoneText.Foreground = ThemeManager.Brush(failed > 0
            ? (ThemeManager.IsLight ? Color.FromRgb(0xA3, 0x2D, 0x2D) : Color.FromRgb(0xF2, 0x8B, 0x82))
            : (ThemeManager.IsLight ? Color.FromRgb(0x1E, 0x7B, 0x3A) : Color.FromRgb(0x8F, 0xD9, 0x9E)));
        DoneText.ToolTip = failed > 0
            ? L10n.F("strip_done_failed_tooltip", done, failed, _settings.RecentlyCompletedMinutes)
            : L10n.F("strip_done_tooltip", done, _settings.RecentlyCompletedMinutes);
        DoneText.Visibility = Visibility.Visible;
    }

    void AnimateAttention(Color target)
    {
        _attentionBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _attentionBackgroundBrush.Color = Color.FromArgb(0, target.R, target.G, target.B);
        if (!SystemParameters.ClientAreaAnimation)
        {
            _attentionBackgroundBrush.Color = target;
            return;
        }

        var animation = new ColorAnimation
        {
            From = _attentionBackgroundBrush.Color,
            To = target,
            Duration = TimeSpan.FromMilliseconds(400),
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        _attentionBackgroundBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    void RenderProvider(UsageSource source, TextBlock target, PaceBar bar)
    {
        bar.Root.Visibility = Visibility.Collapsed;
        var prefix = L10n.T(source == UsageSource.Codex ? "strip_provider_codex" : "strip_provider_claude");
        if (!_usage.TryGetValue(source, out var snapshot))
        {
            SetUnavailable(target, prefix, L10n.T("err_usage_unavailable"));
            return;
        }

        if (snapshot.Health == UsageHealth.SignedOut)
        {
            var message = source == UsageSource.Claude
                ? L10n.T("err_not_signed_in_hint")
                : L10n.T("err_chatgpt_not_signed_in");
            SetUnavailable(target, prefix, message);
            return;
        }

        if (snapshot.Health == UsageHealth.Loading)
        {
            target.Text = $"{prefix} …";
            target.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
            target.ToolTip = L10n.T("updating");
            return;
        }

        var principal = UsageSummary.Compact(snapshot.Windows);
        if (principal is null || snapshot.Health == UsageHealth.Unavailable)
        {
            SetUnavailable(target, prefix, L10n.T("err_usage_unavailable"));
            return;
        }

        var remaining = Math.Round(principal.RemainingPercent);
        target.Text = $"{prefix} {remaining}%";
        target.Foreground = ThemeManager.Brush(snapshot.Health == UsageHealth.Stale
            ? ThemeManager.SubtleText
            : ThemeManager.ColorForAllowance(UsageSummary.Level(
                principal.RemainingPercent,
                _settings.ApproachingBelowPercent,
                _settings.LowBelowPercent)));
        var value = L10n.F("tray_usage_remaining", remaining);
        RenderPaceBar(snapshot, principal, bar);
        target.ToolTip = snapshot.Health == UsageHealth.Stale && snapshot.LastSuccess is DateTimeOffset lastSuccess
            ? $"{prefix}: {value} · {L10n.F("data_stale", lastSuccess.ToLocalTime().ToString("HH:mm"))}"
            : $"{prefix}: {value}";
    }

    /// <summary>Fills the hairline with the session allowance left, coloured by the worst pace of any window.</summary>
    void RenderPaceBar(UsageSnapshot snapshot, UsageWindow shown, PaceBar bar)
    {
        var now = DateTimeOffset.Now;
        var level = snapshot.Windows
            .Select(window => UsagePaceCalculator.Worst(
                window, now, _settings.ApproachingBelowPercent, _settings.LowBelowPercent))
            .DefaultIfEmpty(AllowanceLevel.Normal)
            .Max();
        var brush = ThemeManager.Brush(snapshot.Health == UsageHealth.Stale
            ? ThemeManager.SubtleText
            : ThemeManager.ColorForAllowance(level));
        var remaining = Math.Clamp(shown.RemainingPercent, 0, 100);
        bar.FillColumn.Width = new GridLength(remaining, GridUnitType.Star);
        bar.GapColumn.Width = new GridLength(100 - remaining, GridUnitType.Star);
        bar.Fill.Background = brush;
        bar.Track.Background = brush;
        bar.Root.Visibility = Visibility.Visible;
    }

    void SetUnavailable(TextBlock target, string prefix, string explanation)
    {
        target.Text = $"{prefix} —";
        target.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
        target.ToolTip = $"{prefix}: {explanation}";
    }

    void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not Point start || _dragged || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragged = true;
        _pressPoint = null;
        ReleaseMouseCapture();
        DragStarted?.Invoke();
        try { DragMove(); }
        catch (InvalidOperationException) { /* The button can be released before WPF starts the move. */ }
        finally { DragCompleted?.Invoke(); }
    }

    void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressPoint = null;
        ReleaseMouseCapture();
        if (_dragged)
        {
            _dragged = false;
            e.Handled = true;
            return;
        }
        TogglePaneRequested?.Invoke();
        e.Handled = true;
    }

    void OnRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ContextMenuRequested?.Invoke();
        e.Handled = true;
    }

    sealed record PaceBar(Grid Root, ColumnDefinition FillColumn, ColumnDefinition GapColumn, Border Track, Border Fill);

    void OnClosed(object? sender, EventArgs e)
    {
        L10n.Changed -= ApplyAppearance;
        ThemeManager.Changed -= ApplyAppearance;
    }
}
