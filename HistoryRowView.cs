using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StatusBar.Core.Tasks;
using Color = System.Windows.Media.Color;

namespace ClaudeUsageWidget;

/// <summary>A compact, clickable row for one finished task in the pane's history.</summary>
internal sealed class HistoryRowView : Border
{
    readonly HistoryEntry _entry;
    readonly System.Windows.Controls.Button _removeButton;

    internal event Action<HistoryEntry>? OpenRequested;
    internal event Action<string>? RemoveRequested;

    public HistoryRowView(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(7, 3, 5, 3);
        Margin = new Thickness(0, 0, 0, 2);
        Cursor = System.Windows.Input.Cursors.Hand;
        Background = ThemeManager.Brush(Color.FromArgb(0, 0, 0, 0));

        var icon = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Text = entry.Outcome switch
            {
                HistoryOutcome.Failed => "✕",
                HistoryOutcome.Stopped => "■",
                _ => "✓",
            },
            Foreground = ThemeManager.Brush(entry.Outcome == HistoryOutcome.Failed
                ? ThemeManager.ErrorText
                : ThemeManager.SubtleText),
        };
        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeManager.Brush(ThemeManager.IsLight
                ? Color.FromRgb(0x3A, 0x3B, 0x45)
                : Color.FromRgb(0xC4, 0xC4, 0xCE)),
        };
        var providerName = L10n.T(entry.Provider == AgentProvider.Codex ? "pane_provider_codex" : "pane_provider_claude");
        var meta = new TextBlock
        {
            Text = $"{providerName} · {FormatTime(entry.FinishedAt)}",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = ThemeManager.Brush(ThemeManager.SubtleText),
        };
        _removeButton = new System.Windows.Controls.Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            Foreground = ThemeManager.Brush(ThemeManager.SubtleText),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = L10n.T("pane_history_remove"),
            Visibility = Visibility.Collapsed,
        };
        _removeButton.Click += (_, _) => RemoveRequested?.Invoke(entry.TaskId);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(meta, 2);
        Grid.SetColumn(_removeButton, 3);
        grid.Children.Add(icon);
        grid.Children.Add(title);
        grid.Children.Add(meta);
        grid.Children.Add(_removeButton);
        Child = grid;

        var outcome = L10n.T(entry.Outcome switch
        {
            HistoryOutcome.Failed => "pane_history_failed",
            HistoryOutcome.Stopped => "pane_history_stopped",
            _ => "pane_history_finished",
        });
        ToolTip = $"{outcome} · {entry.FinishedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

        MouseEnter += (_, _) =>
        {
            Background = ThemeManager.Brush(ThemeManager.IsLight ? Color.FromRgb(0xEA, 0xEA, 0xEF) : Color.FromRgb(0x2A, 0x2A, 0x34));
            _removeButton.Visibility = Visibility.Visible;
        };
        MouseLeave += (_, _) =>
        {
            Background = ThemeManager.Brush(Color.FromArgb(0, 0, 0, 0));
            _removeButton.Visibility = Visibility.Collapsed;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (IsWithin(e.OriginalSource as DependencyObject, _removeButton)) return;
            OpenRequested?.Invoke(_entry);
            e.Handled = true;
        };
    }

    // Today shows the time; earlier days add the weekday so a week of history stays readable.
    static string FormatTime(DateTimeOffset finishedAt)
    {
        var local = finishedAt.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", culture)
            : $"{local.ToString("ddd", culture)} {local.ToString("HH:mm", culture)}";
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
}
