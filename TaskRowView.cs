using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>Code-built task row whose controls are reused as task state changes.</summary>
internal sealed class TaskRowView : Border
{
    readonly TextBlock _icon;
    readonly TextBlock _title;
    readonly TextBlock _reason;
    readonly TextBlock _provider;
    readonly System.Windows.Controls.Button _dismissButton;
    AgentTask? _task;

    internal event Action<string>? DismissRequested;
    internal event Action<AgentTask>? FocusRequested;

    /// <summary>Creates a reusable row for one in-memory agent task.</summary>
    public TaskRowView()
    {
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(7, 5, 7, 5);
        Margin = new Thickness(0, 0, 0, 3);

        _icon = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _title = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // The app's fixed reason text ("Asked you a question"), never provider content.
        _reason = new TextBlock
        {
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _provider = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _dismissButton = new System.Windows.Controls.Button
        {
            Content = L10n.T("pane_dismiss"),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(7, 0, 0, 0),
            FontSize = 10,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            Foreground = ThemeManager.Brush(ThemeManager.SubtleText),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        _dismissButton.Click += (_, _) =>
        {
            if (_task is { } task) DismissRequested?.Invoke(task.Id);
        };
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        MouseEnter += (_, _) => UpdateDismissVisibility();
        MouseLeave += (_, _) => UpdateDismissVisibility();
        Cursor = System.Windows.Input.Cursors.Hand;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_icon, 0);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(_title);
        titleStack.Children.Add(_reason);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(_provider, 2);
        Grid.SetColumn(_dismissButton, 3);
        grid.Children.Add(_icon);
        grid.Children.Add(titleStack);
        grid.Children.Add(_provider);
        grid.Children.Add(_dismissButton);
        Child = grid;
    }

    /// <summary>Updates this row from a normalized task, keeping provider evidence in memory only.</summary>
    public void Update(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _task = task;
        _dismissButton.Content = L10n.T("pane_dismiss");
        _dismissButton.ToolTip = L10n.T("pane_dismiss_tooltip");
        _dismissButton.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
        UpdateDismissVisibility();
        var unknown = task.Status == AgentTaskStatus.Unknown;
        var iconColor = task.Status switch
        {
            AgentTaskStatus.NeedsAttention => ThemeManager.IsLight
                ? Color.FromRgb(0x9A, 0x62, 0x10)
                : Color.FromRgb(0xFF, 0xD0, 0x75),
            AgentTaskStatus.Working => ThemeManager.ColorForAllowance(AllowanceLevel.Normal),
            _ => ThemeManager.SubtleText,
        };
        Background = ThemeManager.Brush(ThemeManager.IsLight
            ? Color.FromRgb(0xEA, 0xEA, 0xEF)
            : Color.FromRgb(0x2A, 0x2A, 0x34));
        _icon.Text = task.Status switch
        {
            AgentTaskStatus.NeedsAttention => "⚠",
            AgentTaskStatus.Working => "●",
            AgentTaskStatus.Unknown => "?",
            AgentTaskStatus.Complete => "✓",
            AgentTaskStatus.Failed => "✗",
            _ => "?",
        };
        _icon.Foreground = ThemeManager.Brush(iconColor);
        _title.Text = task.Title;
        _title.Foreground = ThemeManager.Brush(unknown
            ? ThemeManager.SubtleText
            : ThemeManager.IsLight
                ? Color.FromRgb(0x2B, 0x2C, 0x35)
                : Color.FromRgb(0xE0, 0xE0, 0xE7));
        var reason = task.Status switch
        {
            AgentTaskStatus.NeedsAttention => task.AttentionReason,
            AgentTaskStatus.Failed => task.StatusDetail,
            AgentTaskStatus.Complete when task.StatusDetail is StatusBar.Core.Judgment.TurnVerdictPolicy.ReviewDetail
                or StatusBar.Core.Judgment.TurnVerdictPolicy.FollowUpDetail => task.StatusDetail,
            _ => null,
        };
        _reason.Text = reason ?? "";
        _reason.Foreground = ThemeManager.Brush(
            task.Status == AgentTaskStatus.NeedsAttention || task.StatusDetail == StatusBar.Core.Judgment.TurnVerdictPolicy.ReviewDetail
                ? (ThemeManager.IsLight ? Color.FromRgb(0x9A, 0x62, 0x10) : Color.FromRgb(0xFF, 0xD0, 0x75))
                : ThemeManager.SubtleText);
        _reason.Visibility = string.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible;
        var providerName = L10n.T(task.Provider == AgentProvider.Codex
            ? "pane_provider_codex"
            : "pane_provider_claude");
        _provider.Text = $"{providerName} · {FormatAge(task.LastActivity)}";
        _provider.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);

        var tooltip = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.StatusDetail))
            tooltip.Add(L10n.F("pane_status_detail", task.StatusDetail));
        if (!string.IsNullOrWhiteSpace(task.AttentionReason))
            tooltip.Add(L10n.F("pane_attention_reason", task.AttentionReason));
        tooltip.Add(L10n.F("pane_confidence", ConfidenceLabel(task.Confidence)));
        tooltip.Add(FormatActivity(task.LastActivity));
        ToolTip = string.Join(Environment.NewLine, tooltip);
    }

    void OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, _dismissButton)) return;
        if (_task is not { } task) return;
        FocusRequested?.Invoke(task);
        e.Handled = true;
    }

    static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    void UpdateDismissVisibility() =>
        _dismissButton.Visibility = _task is not null && IsMouseOver ? Visibility.Visible : Visibility.Collapsed;

    static string ConfidenceLabel(StateConfidence confidence) => confidence switch
    {
        StateConfidence.Confirmed => L10n.T("pane_confidence_confirmed"),
        StateConfidence.Inferred => L10n.T("pane_confidence_inferred"),
        _ => L10n.T("pane_confidence_stale"),
    };

    // Compact age beside the provider; the tooltip keeps the full wording.
    static string FormatAge(DateTimeOffset lastActivity)
    {
        var age = DateTimeOffset.Now - lastActivity;
        if (age < TimeSpan.FromMinutes(1)) return L10n.T("pane_age_now");
        if (age < TimeSpan.FromHours(1)) return L10n.F("pane_age_minutes", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return L10n.F("pane_age_hours", (int)age.TotalHours);
        return L10n.F("pane_age_days", (int)age.TotalDays);
    }

    static string FormatActivity(DateTimeOffset lastActivity)
    {
        var age = DateTimeOffset.Now - lastActivity;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1)) return L10n.T("pane_activity_just_now");
        if (age < TimeSpan.FromHours(1))
            return L10n.F("pane_activity_minutes_ago", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return L10n.F("pane_activity_hours_ago", (int)age.TotalHours);
        return L10n.F("pane_activity_days_ago", (int)age.TotalDays);
    }
}
