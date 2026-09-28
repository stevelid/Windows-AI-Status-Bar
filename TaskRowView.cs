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
    readonly TextBlock _provider;

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
        _provider = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_icon, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_provider, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(_title);
        grid.Children.Add(_provider);
        Child = grid;
    }

    /// <summary>Updates this row from a normalized task, keeping provider evidence in memory only.</summary>
    public void Update(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
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
        _provider.Text = L10n.T(task.Provider == AgentProvider.Codex
            ? "pane_provider_codex"
            : "pane_provider_claude");
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

    static string ConfidenceLabel(StateConfidence confidence) => confidence switch
    {
        StateConfidence.Confirmed => L10n.T("pane_confidence_confirmed"),
        StateConfidence.Inferred => L10n.T("pane_confidence_inferred"),
        _ => L10n.T("pane_confidence_stale"),
    };

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
