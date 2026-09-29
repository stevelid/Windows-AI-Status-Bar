using WinForms = System.Windows.Forms;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;

namespace ClaudeUsageWidget;

/// <summary>Callbacks the tray controller uses to invoke application-owned commands.</summary>
public sealed record TrayActions(
    Action ToggleStrip,
    Action TogglePane,
    Action RefreshAllowances,
    Action<UsageProviderKind> SignIn,
    Action ShowSettings,
    Action CopyDiagnostics,
    Action SaveDiagnostics,
    Action CheckForUpdates,
    Action CancelUpdate,
    Action Quit);

/// <summary>Owns the notification icon, its context menu and tray-facing status.</summary>
public sealed class TrayController : IDisposable
{
    readonly Settings _settings;
    readonly TrayActions _actions;
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ContextMenuStrip _menu;
    readonly WinForms.ToolStripMenuItem _showStrip;
    readonly WinForms.ToolStripMenuItem _togglePane;
    readonly WinForms.ToolStripMenuItem _refresh;
    readonly WinForms.ToolStripMenuItem _signIn;
    readonly WinForms.ToolStripMenuItem _signInClaude;
    readonly WinForms.ToolStripMenuItem _signInCodex;
    readonly WinForms.ToolStripMenuItem _settingsItem;
    readonly WinForms.ToolStripMenuItem _autoStart;
    readonly WinForms.ToolStripMenuItem _diagnostics;
    readonly WinForms.ToolStripMenuItem _saveDiagnostics;
    readonly WinForms.ToolStripMenuItem _updates;
    readonly WinForms.ToolStripMenuItem _cancelUpdate;
    readonly WinForms.ToolStripMenuItem _quit;
    IReadOnlyDictionary<UsageSource, UsageSnapshot> _usage =
        new Dictionary<UsageSource, UsageSnapshot>();
    (double? Utilization, int ApproachingBelow, int LowBelow, bool Attention)? _iconKey;
    bool _hasAttention;
    bool _syncingAutoStart;
    bool _stripVisible = true;
    bool _paneVisible;
    bool _updateClickPending;
    Action? _balloonClickAction;
    bool _disposed;

    /// <summary>Creates the tray icon and connects menu actions to the application.</summary>
    public TrayController(Settings settings, TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(actions);
        _settings = settings;
        _actions = actions;
        _icon = new WinForms.NotifyIcon
        {
            Text = "Windows AI Status Bar",
            Visible = true,
            Icon = TrayIconRenderer.Render(null),
        };
        _icon.MouseClick += OnIconMouseClick;
        _icon.BalloonTipClicked += OnBalloonTipClicked;

        _menu = new WinForms.ContextMenuStrip();
        _showStrip = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.ToggleStrip());
        _togglePane = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.TogglePane());
        _refresh = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.RefreshAllowances());
        _signIn = new WinForms.ToolStripMenuItem();
        _signInClaude = new WinForms.ToolStripMenuItem("", null,
            (_, _) => _actions.SignIn(UsageProviderKind.Claude));
        _signInCodex = new WinForms.ToolStripMenuItem("", null,
            (_, _) => _actions.SignIn(UsageProviderKind.ChatGpt));
        _signIn.DropDownItems.AddRange([_signInClaude, _signInCodex]);
        _settingsItem = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.ShowSettings());
        _autoStart = new WinForms.ToolStripMenuItem("", null, (_, _) => { }) { CheckOnClick = true };
        _autoStart.CheckedChanged += OnAutoStartCheckedChanged;
        _diagnostics = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.CopyDiagnostics());
        _saveDiagnostics = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.SaveDiagnostics());
        _updates = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.CheckForUpdates());
        _cancelUpdate = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.CancelUpdate())
        {
            Enabled = false,
            Visible = false,
        };
        _quit = new WinForms.ToolStripMenuItem("", null, (_, _) => _actions.Quit());

        _menu.Items.AddRange(
        [
            _showStrip,
            _togglePane,
            _refresh,
            _signIn,
            new WinForms.ToolStripSeparator(),
            _settingsItem,
            _autoStart,
            _diagnostics,
            _saveDiagnostics,
            _updates,
            _cancelUpdate,
            new WinForms.ToolStripSeparator(),
            _quit,
        ]);
        _icon.ContextMenuStrip = _menu;
        L10n.Changed += ApplyLanguage;
        ApplyLanguage();
        UpdateVisibility(stripVisible: true, paneVisible: false);
    }

    /// <summary>Updates the tray icon and tooltip from the latest quota snapshots.</summary>
    public void UpdateUsage(IReadOnlyDictionary<UsageSource, UsageSnapshot> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _usage = usage;
        RenderIcon();

        var lines = new[] { UsageSource.Claude, UsageSource.Codex }
            .Select(source => FormatUsageLine(source, usage.GetValueOrDefault(source)));
        var tip = string.Join("\n", lines);
        var trimmed = tip.Length > 127 ? tip[..127] : tip;
        if (_icon.Text != trimmed) _icon.Text = trimmed;
    }

    /// <summary>Updates the tray attention marker independently of quota refreshes.</summary>
    public void UpdateTaskState(StatusBarState tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _hasAttention = tasks.AttentionCount > 0;
        RenderIcon();
    }

    void RenderIcon()
    {
        // Keep the tray summary aligned with the compact strip's session window.
        var allWindows = _usage.Values
            .Select(snapshot => UsageSummary.Compact(snapshot.Windows))
            .OfType<UsageWindow>()
            .ToArray();
        var principal = UsageSummary.Principal(allWindows);
        var iconKey = (
            principal?.UsedPercent,
            _settings.ApproachingBelowPercent,
            _settings.LowBelowPercent,
            _hasAttention);
        if (_iconKey != iconKey)
        {
            _iconKey = iconKey;
            var oldIcon = _icon.Icon;
            _icon.Icon = TrayIconRenderer.Render(
                principal?.UsedPercent,
                _settings.ApproachingBelowPercent,
                _settings.LowBelowPercent,
                _hasAttention);
            oldIcon?.Dispose();
        }
    }

    /// <summary>Updates Show/Hide and Expand/Collapse menu text to match current window state.</summary>
    public void UpdateVisibility(bool stripVisible, bool paneVisible)
    {
        _stripVisible = stripVisible;
        _paneVisible = paneVisible;
        _showStrip.Text = L10n.T(stripVisible ? "menu_hide_strip" : "menu_show_strip");
        _togglePane.Text = L10n.T(paneVisible ? "menu_collapse_pane" : "menu_expand_pane");
    }

    /// <summary>Shows the shared context menu at the current pointer position.</summary>
    public void ShowContextMenuAtCursor() => _menu.Show(WinForms.Cursor.Position);

    /// <summary>Enables or hides the tray cancellation command while a download can be stopped.</summary>
    public void SetUpdateCancellationEnabled(bool enabled)
    {
        _cancelUpdate.Visible = enabled;
        _cancelUpdate.Enabled = enabled;
    }

    /// <summary>Shows a short system tray notification.</summary>
    public void ShowBalloonTip(int timeout, string title, string text, WinForms.ToolTipIcon icon)
    {
        _updateClickPending = false;
        _balloonClickAction = null;
        _icon.ShowBalloonTip(timeout, title, text, icon);
    }

    /// <summary>Shows an attention balloon whose click opens the details pane.</summary>
    public void ShowAttention(string title, string text)
    {
        _updateClickPending = false;
        _balloonClickAction = _actions.TogglePane;
        _icon.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Warning);
    }

    /// <summary>Shows a finished-task balloon whose click opens the details pane.</summary>
    public void ShowCompletion(string title, string text, bool failed)
    {
        _updateClickPending = false;
        _balloonClickAction = _actions.TogglePane;
        _icon.ShowBalloonTip(8000, title, text,
            failed ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info);
    }

    /// <summary>Shows an update notice and opens the update flow if the user clicks it.</summary>
    public void ShowUpdateAvailable(string latestVersion)
    {
        _updateClickPending = true;
        _balloonClickAction = null;
        _icon.ShowBalloonTip(
            8000,
            "Windows AI Status Bar",
            L10n.F("update_balloon", latestVersion),
            WinForms.ToolTipIcon.Info);
    }

    /// <summary>Releases the icon, menu and language subscription.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        L10n.Changed -= ApplyLanguage;
        _icon.MouseClick -= OnIconMouseClick;
        _icon.BalloonTipClicked -= OnBalloonTipClicked;
        _balloonClickAction = null;
        _autoStart.CheckedChanged -= OnAutoStartCheckedChanged;
        _icon.Visible = false;
        var icon = _icon.Icon;
        _icon.Icon = null;
        icon?.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    string FormatUsageLine(UsageSource source, UsageSnapshot? snapshot)
    {
        var provider = L10n.T(source == UsageSource.Claude ? "provider_claude" : "provider_chatgpt");
        var window = snapshot is null ? null : UsageSummary.Compact(snapshot.Windows);
        if (window is null)
            return $"{provider}: {L10n.T("tray_usage_unavailable")}";

        var remaining = L10n.F("tray_usage_remaining", Math.Round(window.RemainingPercent));
        if (snapshot!.Health is UsageHealth.Stale or UsageHealth.SignedOut)
            remaining += L10n.T("tray_usage_stale");
        return $"{provider}: {remaining}";
    }

    void ApplyLanguage()
    {
        _refresh.Text = L10n.T("menu_refresh");
        _signIn.Text = L10n.T("menu_sign_in");
        _signInClaude.Text = L10n.T("provider_claude");
        _signInCodex.Text = L10n.T("menu_sign_in_codex");
        _settingsItem.Text = L10n.T("menu_settings");
        _autoStart.Text = L10n.T("menu_autostart");
        _diagnostics.Text = L10n.T("menu_copy_diagnostics");
        _saveDiagnostics.Text = L10n.T("menu_save_diagnostic_bundle");
        _updates.Text = L10n.T("menu_check_update");
        _cancelUpdate.Text = L10n.T("update_cancel");
        _quit.Text = L10n.T("menu_exit");
        UpdateVisibility(_stripVisible, _paneVisible);
        SyncAutoStartCheck();
        UpdateUsage(_usage);
    }

    void SyncAutoStartCheck()
    {
        _syncingAutoStart = true;
        _autoStart.Checked = AutoStart.IsEnabled();
        _syncingAutoStart = false;
    }

    void OnAutoStartCheckedChanged(object? sender, EventArgs e)
    {
        if (_syncingAutoStart) return;
        var result = _autoStart.Checked ? AutoStart.TryEnable() : AutoStart.TryDisable();
        SyncAutoStartCheck();
        if (!result.Succeeded || result.Detail is not null)
        {
            ShowBalloonTip(
                8000,
                "Windows AI Status Bar",
                L10n.F("autostart_problem", result.Detail ?? "UnknownFailure"),
                WinForms.ToolTipIcon.Warning);
        }
    }

    void OnIconMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left) _actions.ToggleStrip();
    }

    void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_balloonClickAction is { } action)
        {
            _balloonClickAction = null;
            action();
            return;
        }

        if (!_updateClickPending) return;
        _updateClickPending = false;
        _actions.CheckForUpdates();
    }
}
