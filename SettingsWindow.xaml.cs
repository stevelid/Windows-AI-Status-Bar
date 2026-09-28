using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageWidget;

public partial class SettingsWindow : Window
{
    static readonly int[] IntervalSteps = [60, 90, 120, 300];
    static readonly int[] AutoCollapseSteps = [0, 30, 60, 120, 300];

    readonly Settings _settings;
    readonly Action _onChanged;
    bool _initializing = true;
    bool _updatingThresholds;

    public SettingsWindow(Settings settings, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onChanged);
        _settings = settings;
        _onChanged = onChanged;
        _settings.Normalize();
        InitializeComponent();

        LanguageCombo.SelectedIndex = L10n.Lang == UiLanguage.En ? 1 : 0;
        ThemeCombo.SelectedIndex = ThemeManager.IsLight ? 1 : 0;
        IntervalCombo.SelectedIndex = Array.FindIndex(
            IntervalSteps,
            seconds => _settings.RefreshIntervalSec <= seconds);
        if (IntervalCombo.SelectedIndex < 0) IntervalCombo.SelectedIndex = IntervalSteps.Length - 1;
        OpacitySlider.Value = _settings.BgTransparency;
        CodexPathBox.Text = _settings.CodexExecutablePath ?? "";
        CodexHomeBox.Text = _settings.CodexHomeOverride ?? "";
        ClaudeCodeHomeBox.Text = _settings.ClaudeCodeHomeOverride ?? "";
        ClaudeHooksCheckBox.IsChecked = _settings.UseClaudeCodeHooks;
        CoworkRootBox.Text = _settings.CoworkRootOverride ?? "";
        NotificationsCheckBox.IsChecked = _settings.NotificationsEnabled;
        DemoTasksCheckBox.IsChecked = _settings.DemoTasks;
        RecentCompletedSlider.Value = _settings.RecentlyCompletedMinutes;
        AttentionLabelBox.Text = _settings.AttentionLabel;
        ApproachingSlider.Value = _settings.ApproachingBelowPercent;
        LowSlider.Value = _settings.LowBelowPercent;
        PopulateAutoCollapseOptions();
        PopulateMonitors();

        ApplyAppearance();
        _initializing = false;
    }

    void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.Language = LanguageCombo.SelectedIndex == 1 ? "en" : "zh";
        SaveAndApply();
        L10n.Set(LanguageCombo.SelectedIndex == 1 ? UiLanguage.En : UiLanguage.ZhHant);
        ApplyAppearance();
    }

    void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.Theme = ThemeCombo.SelectedIndex == 1 ? "light" : "dark";
        SaveAndApply();
        ThemeManager.Set(ThemeCombo.SelectedIndex == 1);
        ApplyAppearance();
    }

    void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || IntervalCombo.SelectedIndex < 0) return;
        _settings.RefreshIntervalSec = IntervalSteps[IntervalCombo.SelectedIndex];
        SaveAndApply();
    }

    void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        if (_initializing) return;
        _settings.BgTransparency = (int)OpacitySlider.Value;
        SaveAndApply();
    }

    void OnNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.NotificationsEnabled = NotificationsCheckBox.IsChecked == true;
        SaveAndApply();
    }

    void OnDemoTasksChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.DemoTasks = DemoTasksCheckBox.IsChecked == true;
        SaveAndApply();
    }

    void OnRecentCompletedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RecentCompletedValue is null) return;
        var minutes = (int)Math.Round(RecentCompletedSlider.Value);
        RecentCompletedValue.Text = minutes.ToString();
        if (_initializing) return;
        _settings.RecentlyCompletedMinutes = minutes;
        SaveAndApply();
    }

    void OnPaneAutoCollapseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PaneAutoCollapseCombo.SelectedItem is not ComboBoxItem { Tag: int seconds }) return;
        _settings.PaneAutoCollapseSeconds = seconds;
        SaveAndApply();
    }

    void OnAttentionLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AttentionLabel = AttentionLabelBox.Text.Trim();
        _settings.Normalize();
        AttentionLabelBox.Text = _settings.AttentionLabel;
        SaveAndApply();
    }

    void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MonitorCombo.SelectedItem is not ComboBoxItem { Tag: string deviceName }) return;
        var primary = WinForms.Screen.PrimaryScreen?.DeviceName;
        _settings.MonitorDeviceName = string.Equals(deviceName, primary, StringComparison.OrdinalIgnoreCase)
            ? null
            : deviceName;
        SaveAndApply();
    }

    void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _updatingThresholds) return;
        _updatingThresholds = true;
        var approaching = (int)Math.Round(ApproachingSlider.Value);
        var low = (int)Math.Round(LowSlider.Value);
        if (ReferenceEquals(sender, ApproachingSlider) && approaching <= low)
        {
            low = approaching - 1;
            LowSlider.Value = low;
        }
        else if (ReferenceEquals(sender, LowSlider) && low >= approaching)
        {
            approaching = low + 1;
            ApproachingSlider.Value = approaching;
        }

        _settings.ApproachingBelowPercent = approaching;
        _settings.LowBelowPercent = low;
        _settings.Normalize();
        RefreshThresholdValues();
        _updatingThresholds = false;
        SaveAndApply();
    }

    void OnCodexBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.T("settings_codex_path"),
            Filter = "Codex executable|codex.exe;codex.cmd;codex.bat|Executable files|*.exe;*.cmd;*.bat|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        CodexPathBox.Text = dialog.FileName;
        SaveCodexPath();
    }

    void OnCodexPathLostFocus(object sender, RoutedEventArgs e) => SaveCodexPath();
    void OnCodexHomeLostFocus(object sender, RoutedEventArgs e) =>
        SavePath(CodexHomeBox, value => _settings.CodexHomeOverride = value);
    void OnClaudeCodeHomeLostFocus(object sender, RoutedEventArgs e) =>
        SavePath(ClaudeCodeHomeBox, value => _settings.ClaudeCodeHomeOverride = value);
    void OnClaudeHooksChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var enabled = ClaudeHooksCheckBox.IsChecked == true;
        try
        {
            ClaudeHookSettingsInstaller.SetInstalled(enabled, _settings);
        }
        catch
        {
            _initializing = true;
            ClaudeHooksCheckBox.IsChecked = _settings.UseClaudeCodeHooks;
            _initializing = false;
            System.Windows.MessageBox.Show(this, L10n.T("settings_claude_hooks_error"), L10n.T("settings_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.UseClaudeCodeHooks = enabled;
        SaveAndApply();
    }
    void OnCoworkRootLostFocus(object sender, RoutedEventArgs e) =>
        SavePath(CoworkRootBox, value => _settings.CoworkRootOverride = value);

    void SaveCodexPath() => SavePath(CodexPathBox, value => _settings.CodexExecutablePath = value);

    void SavePath(System.Windows.Controls.TextBox box, Action<string?> setValue)
    {
        if (_initializing) return;
        var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        setValue(value);
        box.Text = value ?? "";
        SaveAndApply();
    }

    void PopulateAutoCollapseOptions()
    {
        var values = AutoCollapseSteps.Append(_settings.PaneAutoCollapseSeconds)
            .Distinct()
            .Order()
            .ToArray();
        foreach (var seconds in values)
        {
            PaneAutoCollapseCombo.Items.Add(new ComboBoxItem { Tag = seconds });
        }
        PaneAutoCollapseCombo.SelectedItem = PaneAutoCollapseCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is int value && value == _settings.PaneAutoCollapseSeconds);
    }

    void PopulateMonitors()
    {
        var screens = WinForms.Screen.AllScreens;
        foreach (var screen in screens)
            MonitorCombo.Items.Add(new ComboBoxItem { Tag = screen.DeviceName });

        var selected = screens.FirstOrDefault(screen => string.Equals(
            screen.DeviceName,
            _settings.MonitorDeviceName,
            StringComparison.OrdinalIgnoreCase))
            ?? WinForms.Screen.PrimaryScreen
            ?? screens.FirstOrDefault();
        if (selected is not null)
        {
            MonitorCombo.SelectedItem = MonitorCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    selected.DeviceName,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    void ApplyAppearance()
    {
        Title = L10n.T("settings_title");
        LanguageLabel.Text = L10n.T("settings_language");
        ThemeLabel.Text = L10n.T("settings_theme");
        ((ComboBoxItem)ThemeCombo.Items[0]).Content = L10n.T("theme_dark");
        ((ComboBoxItem)ThemeCombo.Items[1]).Content = L10n.T("theme_light");
        IntervalLabel.Text = L10n.T("settings_interval");
        ((ComboBoxItem)IntervalCombo.Items[0]).Content = L10n.T("interval_60");
        ((ComboBoxItem)IntervalCombo.Items[1]).Content = L10n.T("interval_90");
        ((ComboBoxItem)IntervalCombo.Items[2]).Content = L10n.T("interval_120");
        ((ComboBoxItem)IntervalCombo.Items[3]).Content = L10n.T("interval_300");
        OpacityLabel.Text = L10n.T("settings_opacity");
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        OpacityHint.Text = L10n.T("settings_opacity_hint");
        CodexPathLabel.Text = L10n.T("settings_codex_path");
        CodexBrowseButton.Content = L10n.T("settings_codex_browse");
        CodexPathHint.Text = L10n.T("settings_codex_hint");
        CodexHomeLabel.Text = L10n.T("settings_codex_home");
        ClaudeCodeHomeLabel.Text = L10n.T("settings_claude_code_home");
        ClaudeHooksCheckBox.Content = L10n.T("settings_claude_hooks");
        ClaudeHooksHint.Text = L10n.T("settings_claude_hooks_hint");
        ClaudeHooksPreview.Text = ClaudeHookSettingsInstaller.BuildPreview();
        CoworkRootLabel.Text = L10n.T("settings_cowork_root");
        NotificationsCheckBox.Content = L10n.T("settings_notifications");
        DemoTasksCheckBox.Content = L10n.T("settings_demo_tasks");
        RecentCompletedLabel.Text = L10n.T("settings_recent_completed");
        RecentCompletedValue.Text = ((int)Math.Round(RecentCompletedSlider.Value)).ToString();
        PaneAutoCollapseLabel.Text = L10n.T("settings_pane_auto_collapse");
        AttentionLabelText.Text = L10n.T("settings_attention_label");
        MonitorLabel.Text = L10n.T("settings_monitor");
        ApproachingLabel.Text = L10n.T("settings_threshold_approaching");
        LowLabel.Text = L10n.T("settings_threshold_low");
        RefreshThresholdValues();
        RefreshAutoCollapseLabels();
        RefreshMonitorLabels();

        var bg = ThemeManager.IsLight
            ? Color.FromRgb(0xFA, 0xFA, 0xFC)
            : Color.FromRgb(0x1E, 0x1E, 0x28);
        Background = new SolidColorBrush(bg);
        var fg = ThemeManager.Brush(ThemeManager.TitleText);
        foreach (var label in new[]
                 {
                     LanguageLabel, ThemeLabel, IntervalLabel, OpacityLabel,
                     OpacityValue, CodexPathLabel, CodexHomeLabel, ClaudeCodeHomeLabel, ClaudeHooksHint, CoworkRootLabel,
                     RecentCompletedLabel, PaneAutoCollapseLabel, AttentionLabelText,
                     MonitorLabel, ApproachingLabel, LowLabel,
                 })
        {
            label.Foreground = fg;
        }
        NotificationsCheckBox.Foreground = fg;
        DemoTasksCheckBox.Foreground = fg;
        OpacityHint.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
        CodexPathHint.Foreground = ThemeManager.Brush(ThemeManager.SubtleText);
    }

    void RefreshAutoCollapseLabels()
    {
        foreach (var item in PaneAutoCollapseCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not int seconds) continue;
            item.Content = seconds == 0
                ? L10n.T("settings_auto_collapse_off")
                : L10n.F("settings_auto_collapse_seconds", seconds);
        }
    }

    void RefreshMonitorLabels()
    {
        var screens = WinForms.Screen.AllScreens;
        var primaryName = WinForms.Screen.PrimaryScreen?.DeviceName;
        for (var index = 0; index < MonitorCombo.Items.Count; index++)
        {
            if (MonitorCombo.Items[index] is not ComboBoxItem item || item.Tag is not string deviceName)
                continue;
            var screen = screens.FirstOrDefault(candidate => string.Equals(
                candidate.DeviceName,
                deviceName,
                StringComparison.OrdinalIgnoreCase));
            if (screen is null) continue;
            item.Content = string.Equals(deviceName, primaryName, StringComparison.OrdinalIgnoreCase)
                ? L10n.T("settings_monitor_primary")
                : L10n.F(
                    "settings_monitor_display",
                    index + 1,
                    screen.Bounds.Width,
                    screen.Bounds.Height);
        }
    }

    void RefreshThresholdValues()
    {
        ApproachingValue.Text = $"{(int)Math.Round(ApproachingSlider.Value)}%";
        LowValue.Text = $"{(int)Math.Round(LowSlider.Value)}%";
    }

    void SaveAndApply()
    {
        if (_initializing) return;
        _settings.Normalize();
        _settings.Save();
        _onChanged();
    }
}
