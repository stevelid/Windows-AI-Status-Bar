using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>The compact, docked status strip above the Windows taskbar.</summary>
public partial class StatusStripWindow : Window
{
    readonly Settings _settings;
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _usage = new Dictionary<UsageSource, UsageSnapshot>();
    StatusBarState _tasks = StatusBarState.Empty;
    string? _transientMessage;

    /// <summary>Raised when the user clicks the strip to toggle the details pane.</summary>
    public event Action? TogglePaneRequested;

    /// <summary>Raised when the user right-clicks the strip.</summary>
    public event Action? ContextMenuRequested;

    /// <summary>Creates the strip from current settings and applies its initial appearance.</summary>
    public StatusStripWindow(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        InitializeComponent();
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
        RenderProvider(UsageSource.Codex, CodexText);
        RenderProvider(UsageSource.Claude, ClaudeText);

        WorkingText.Text = $"● {_tasks.WorkingCount}";
        WorkingText.Foreground = ThemeManager.Brush(ThemeManager.LabelText);
        WorkingText.ToolTip = L10n.F("strip_working_tooltip", _tasks.WorkingCount);

        if (_tasks.AttentionCount > 0)
        {
            var label = string.IsNullOrWhiteSpace(_settings.AttentionLabel)
                ? "STEVE"
                : _settings.AttentionLabel.Trim();
            if (label.Length > 12) label = label[..12];
            AttentionText.Text = $"⚠ {label} {_tasks.AttentionCount}";
            AttentionPill.Background = ThemeManager.Brush(ThemeManager.IsLight
                ? Color.FromRgb(0xF3, 0xD3, 0x94)
                : Color.FromRgb(0x61, 0x49, 0x25));
            AttentionText.Foreground = ThemeManager.Brush(ThemeManager.IsLight
                ? Color.FromRgb(0x62, 0x42, 0x0D)
                : Color.FromRgb(0xFF, 0xDC, 0x9A));
            AttentionPill.ToolTip = L10n.F("strip_attention_tooltip", _tasks.AttentionCount);
            AttentionPill.Visibility = Visibility.Visible;
        }
        else
        {
            AttentionPill.Visibility = Visibility.Collapsed;
        }

        if (_transientMessage is null)
            ToolTip = $"{CodexText.ToolTip}\n{ClaudeText.ToolTip}\n{WorkingText.ToolTip}";
    }

    void RenderProvider(UsageSource source, TextBlock target)
    {
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
        target.ToolTip = snapshot.Health == UsageHealth.Stale && snapshot.LastSuccess is DateTimeOffset lastSuccess
            ? $"{prefix}: {value} · {L10n.F("data_stale", lastSuccess.ToLocalTime().ToString("HH:mm"))}"
            : $"{prefix}: {value}";
    }

    void SetUnavailable(TextBlock target, string prefix, string explanation)
    {
        target.Text = $"{prefix} —";
        target.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
        target.ToolTip = $"{prefix}: {explanation}";
    }

    void OnLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        TogglePaneRequested?.Invoke();
        e.Handled = true;
    }

    void OnRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ContextMenuRequested?.Invoke();
        e.Handled = true;
    }

    void OnClosed(object? sender, EventArgs e)
    {
        L10n.Changed -= ApplyAppearance;
        ThemeManager.Changed -= ApplyAppearance;
    }
}
