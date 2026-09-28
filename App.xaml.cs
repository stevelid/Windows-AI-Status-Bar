using System.Windows;
using System.Security.Principal;
using ClaudeUsageWidget.Providers;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace ClaudeUsageWidget;

public partial class App : System.Windows.Application
{
    readonly UsageService _claudeService = new();
    readonly object _chatGptServiceGate = new();

    UsageMonitor? _usageMonitor;
    AgentStateService? _agentStateService;
    DockController? _dockController;
    ChatGptUsageService? _chatGptService;
    string? _chatGptServicePath;
    Settings _settings = null!;
    StatusStripWindow _widget = null!;
    WinForms.NotifyIcon _tray = null!;
    StatusBarState _taskState = StatusBarState.Empty;
    bool _loginWindowOpen;
    bool _demoTasksRequested;
    bool _exitStarted;
    int? _lastTrayPct;
    Mutex? _singleInstanceMutex;
    EventWaitHandle? _activationSignal;
    RegisteredWaitHandle? _activationWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Initialize();

        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        Log.Write($"=== 啟動 v{typeof(App).Assembly.GetName().Version} pid={Environment.ProcessId} args=[{string.Join(' ', e.Args)}] path={Environment.ProcessPath}");
        if (AppPaths.ResolutionNote.Length > 0)
            Log.Write($"資料路徑備援: {AppPaths.ResolutionNote} -> {AppPaths.DataDir}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        if (e.Args.Contains("--autostart"))
        {
            Log.Write("開機自動啟動：延遲 15 秒等系統就緒");
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        try
        {
            _demoTasksRequested = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            StartupCore();
            StartActivationListener();
            Log.Write("啟動完成");
        }
        catch (Exception ex)
        {
            Log.Error("啟動失敗", ex);
            throw;
        }
    }

    static string InstanceName(string purpose)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"Local\\WindowsAIStatusBar-{purpose}-{sid}";
    }

    bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, InstanceName("instance"), out var createdNew);
        if (createdNew)
        {
            _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName("activate"));
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    static void SignalExistingInstance()
    {
        // The first copy creates the event immediately after the mutex. A short retry
        // handles a second launch during that tiny startup window without polling later.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(InstanceName("activate"));
                signal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    void StartActivationListener()
    {
        if (_activationSignal is null) return;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowExistingInstance),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    void ShowExistingInstance()
    {
        _settings.WidgetVisible = true;
        _settings.Save();
        if (!_widget.IsVisible) _widget.Show();
        _dockController?.Redock();
        _widget.Activate();
    }

    void StartupCore()
    {
        _settings = Settings.Load();
        string? autoStartNotice = null;

        L10n.Init(_settings.Language switch
        {
            "en" => UiLanguage.En,
            "zh" => UiLanguage.ZhHant,
            _ => System.Globalization.CultureInfo.CurrentUICulture.Name
                     .StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                 ? UiLanguage.ZhHant
                 : UiLanguage.En,
        });
        ThemeManager.Init(_settings.Theme == "light");

        if (!_settings.FirstRunDone)
        {
            var result = AutoStart.TryEnable();
            if (!result.Succeeded || result.Detail is not null)
                autoStartNotice = result.Detail ?? "UnknownFailure";
            _settings.FirstRunDone = true;
            _settings.Save();
        }
        else if (AutoStart.IsEnabled())
        {
            var result = AutoStart.TryEnable();
            if (!result.Succeeded)
                autoStartNotice = result.Detail ?? "UnknownFailure";
            else if (result.Detail is not null)
                Log.Write($"Auto-start shortcut is active; legacy registry cleanup warning: {result.Detail}");
        }

        _widget = new StatusStripWindow(_settings);
        _dockController = new DockController(_widget);
        _widget.ContextMenuRequested += ShowTrayContextMenu;

        UpdateService.CleanupOldBinary();
        UpdateService.CleanupStaleTemporaryDirectories();
        SetupTray();
        if (autoStartNotice is not null)
        {
            _tray.ShowBalloonTip(
                8000,
                "AI Usage Widget",
                L10n.F("autostart_problem", autoStartNotice),
                WinForms.ToolTipIcon.Warning);
        }
        if (_settings.WidgetVisible) _widget.Show();

        _usageMonitor = new UsageMonitor(
            [
                new ClaudeUsageProvider(_claudeService),
                new CodexUsageProvider(GetChatGptService),
            ],
            TimeProvider.System,
            () => TimeSpan.FromSeconds(BaseIntervalSec));
        _usageMonitor.Changed += OnUsageChanged;
        UpdateStrip();
        UpdateTray();
        _usageMonitor.Start();

        if (_demoTasksRequested || _settings.DemoTasks)
        {
            _agentStateService = new AgentStateService(
                [
                    new DemoTaskProvider(AgentProvider.Codex, TimeProvider.System),
                    new DemoTaskProvider(AgentProvider.Claude, TimeProvider.System),
                ],
                TimeProvider.System,
                StateServiceOptions.Default);
            _taskState = _agentStateService.Current;
            _agentStateService.StateChanged += OnTaskStateChanged;
            UpdateStrip();
        }

        if (_widget.ActiveProvider == UsageProviderKind.Claude && !_claudeService.HasTokens)
        {
            _widget.ShowError(L10n.T("err_not_signed_in_hint"));
            _ = SignInClaudeAsync(force: false);
        }

        _ = AutoCheckUpdatesAsync();
    }

    // ---------- self-update ----------

    UpdateInfo? _pendingUpdate;
    bool _updating;
    bool _downloadCanBeCancelled;
    CancellationTokenSource? _updateCancellation;

    async Task AutoCheckUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            _pendingUpdate = await UpdateService.CheckAsync();
            if (_pendingUpdate is not null)
            {
                Log.Write($"發現新版本 v{_pendingUpdate.Latest}");
                _tray.BalloonTipClicked -= OnUpdateBalloonClicked;
                _tray.BalloonTipClicked += OnUpdateBalloonClicked;
                _tray.ShowBalloonTip(8000, "AI Usage Widget",
                    L10n.F("update_balloon", _pendingUpdate.Latest), WinForms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("自動檢查更新失敗", ex);
        }
    }

    void OnUpdateBalloonClicked(object? sender, EventArgs e) => _ = CheckForUpdatesAsync(interactive: true);

    async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var info = _pendingUpdate ?? await UpdateService.CheckAsync();
            if (info is null)
            {
                if (interactive)
                    MessageBox.Show(L10n.F("update_none", UpdateService.Current),
                        L10n.T("update_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                L10n.F("update_found", info.Latest, UpdateService.Current),
                L10n.T("update_title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _settings.WidgetVisible = true;
            _settings.Save();
            if (!_widget.IsVisible) _widget.Show();
            _widget.Activate();

            _updateCancellation = new CancellationTokenSource();
            var progress = new Progress<UpdateProgress>(ShowUpdateProgress);
            await UpdateService.DownloadAndApplyAsync(info, progress, _updateCancellation.Token);
            ExitAfterUpdate();
        }
        catch (OperationCanceledException) when (_updateCancellation?.IsCancellationRequested == true)
        {
            Log.Write("使用者取消更新下載");
            _widget.ShowNotice(L10n.T("update_cancelled"));
        }
        catch (Exception ex)
        {
            Log.Error("更新失敗", ex);
            if (interactive)
                MessageBox.Show(L10n.F("update_failed", ex.Message),
                    L10n.T("update_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCanBeCancelled = false;
            SetUpdateCancellationEnabled(false);
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            _widget?.HideUpdateProgress();
            _updating = false;
        }
    }

    void ShowUpdateProgress(UpdateProgress progress)
    {
        _downloadCanBeCancelled = progress.Stage == UpdateStage.Downloading;
        SetUpdateCancellationEnabled(_downloadCanBeCancelled);

        string message;
        double? percent = null;
        switch (progress.Stage)
        {
            case UpdateStage.Downloading when progress.TotalBytes is long total && total > 0:
                percent = progress.CompletedBytes * 100.0 / total;
                message = L10n.F(
                    "update_download_progress",
                    Math.Clamp((int)Math.Round(percent.Value), 0, 100),
                    (progress.CompletedBytes / 1024d / 1024d).ToString("0.0"),
                    (total / 1024d / 1024d).ToString("0.0"));
                break;
            case UpdateStage.Downloading:
                message = L10n.T("update_download_unknown");
                break;
            case UpdateStage.Verifying:
                message = L10n.T("update_verifying");
                if (progress.TotalBytes is long verifyTotal && verifyTotal > 0)
                    percent = progress.CompletedBytes * 100.0 / verifyTotal;
                break;
            case UpdateStage.Extracting:
                message = L10n.T("update_extracting");
                break;
            case UpdateStage.Applying:
                message = L10n.T("update_applying");
                break;
            default:
                message = L10n.T("update_restarting");
                break;
        }

        _widget.ShowUpdateProgress(message, percent, _downloadCanBeCancelled);
    }

    void CancelPendingUpdate()
    {
        if (_updating && _downloadCanBeCancelled)
            _updateCancellation?.Cancel();
    }

    // ---------- tray ----------

    WinForms.ToolStripMenuItem _trayShowHide = null!;
    WinForms.ToolStripMenuItem _trayResetPosition = null!;
    WinForms.ToolStripMenuItem _trayRefresh = null!;
    WinForms.ToolStripMenuItem _trayProvider = null!;
    WinForms.ToolStripMenuItem _trayClaude = null!;
    WinForms.ToolStripMenuItem _trayChatGpt = null!;
    WinForms.ToolStripMenuItem _traySettings = null!;
    WinForms.ToolStripMenuItem _trayDiagnostics = null!;
    WinForms.ToolStripMenuItem _trayUpdate = null!;
    WinForms.ToolStripMenuItem _trayCancelUpdate = null!;
    WinForms.ToolStripMenuItem _trayRelogin = null!;
    WinForms.ToolStripMenuItem _trayExit = null!;

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "AI Usage Widget",
            Visible = true,
            Icon = TrayIconRenderer.Render(null),
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left) ToggleWidget();
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_trayShowHide = new WinForms.ToolStripMenuItem("", null, (_, _) => ToggleWidget()));
        menu.Items.Add(_trayResetPosition = new WinForms.ToolStripMenuItem("", null, (_, _) => ResetWidgetPosition()));
        menu.Items.Add(_trayRefresh = new WinForms.ToolStripMenuItem("", null, (_, _) => _usageMonitor?.RefreshNow()));
        _trayProvider = new WinForms.ToolStripMenuItem();
        _trayProvider.DropDownItems.Add(_trayClaude = new WinForms.ToolStripMenuItem("Claude", null,
            (_, _) => ActivateProvider(UsageProviderKind.Claude)));
        _trayProvider.DropDownItems.Add(_trayChatGpt = new WinForms.ToolStripMenuItem("ChatGPT", null,
            (_, _) => ActivateProvider(UsageProviderKind.ChatGpt)));
        menu.Items.Add(_trayProvider);
        menu.Items.Add(_traySettings = new WinForms.ToolStripMenuItem("", null, (_, _) => ShowSettings()));
        menu.Items.Add(_trayDiagnostics = new WinForms.ToolStripMenuItem("", null, (_, _) => CopyDiagnostics()));
        menu.Items.Add(_trayUpdate = new WinForms.ToolStripMenuItem("", null, (_, _) => _ = CheckForUpdatesAsync(interactive: true)));
        menu.Items.Add(_trayCancelUpdate = new WinForms.ToolStripMenuItem("", null, (_, _) => CancelPendingUpdate())
        {
            Enabled = false,
            Visible = false,
        });
        menu.Items.Add(_trayRelogin = new WinForms.ToolStripMenuItem("", null, (_, _) => _ = SignInActiveProviderAsync(force: true)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayExit = new WinForms.ToolStripMenuItem("", null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;

        ApplyTrayLanguage();
        UpdateProviderChecks();
        L10n.Changed += ApplyTrayLanguage;
    }

    void ApplyTrayLanguage()
    {
        _trayShowHide.Text = L10n.T("menu_showhide");
        _trayResetPosition.Text = L10n.T("menu_reset_position");
        _trayRefresh.Text = L10n.T("menu_refresh");
        _trayProvider.Text = L10n.T("menu_provider");
        _trayClaude.Text = L10n.T("provider_claude");
        _trayChatGpt.Text = L10n.T("provider_chatgpt");
        _traySettings.Text = L10n.T("menu_settings");
        _trayDiagnostics.Text = L10n.T("menu_copy_diagnostics");
        _trayUpdate.Text = L10n.T("menu_check_update");
        _trayCancelUpdate.Text = L10n.T("update_cancel");
        _trayRelogin.Text = L10n.T("menu_relogin");
        _trayExit.Text = L10n.T("menu_exit");
        UpdateTray();
    }

    void UpdateProviderChecks()
    {
        if (_trayClaude is null) return;
        _trayClaude.Checked = _widget.ActiveProvider == UsageProviderKind.Claude;
        _trayChatGpt.Checked = _widget.ActiveProvider == UsageProviderKind.ChatGpt;
    }

    SettingsWindow? _settingsWindow;

    void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplySettingsChanges);
        _settingsWindow.Show();
    }

    void CopyDiagnostics()
    {
        try
        {
            var providers = _usageMonitor?.Current.Values
                .Select(snapshot => new ProviderDiagnostic(
                    snapshot.Source,
                    snapshot.Health,
                    snapshot.LastSuccess,
                    snapshot.StatusCode))
                ?? Enumerable.Empty<ProviderDiagnostic>();
            var report = DiagnosticsService.BuildReport(
                typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0),
                providers,
                DiagnosticsService.InspectCodex(_settings.CodexExecutablePath));
            System.Windows.Clipboard.SetText(report);
            _tray.ShowBalloonTip(
                3500,
                "AI Usage Widget",
                L10n.T("diagnostics_copied"),
                WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Copy diagnostics failed", ex);
            MessageBox.Show(
                L10n.F("diagnostics_copy_failed", ex.GetType().Name),
                "AI Usage Widget",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    void ApplySettingsChanges()
    {
        _widget.ApplyScale();
        _widget.ApplyAppearance();
        _dockController?.Redock();

        if (!string.Equals(_chatGptServicePath, _settings.CodexExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            lock (_chatGptServiceGate)
            {
                _chatGptService?.Dispose();
                _chatGptService = null;
                _chatGptServicePath = null;
            }
        }
        _usageMonitor?.RefreshNow();
    }

    void ToggleWidget()
    {
        if (_widget.IsVisible) HideWidget();
        else
        {
            _settings.WidgetVisible = true;
            _settings.Save();
            _widget.Show();
            _widget.Activate();
        }
    }

    void HideWidget()
    {
        _settings.WidgetVisible = false;
        _settings.Save();
        _widget.Hide();
    }

    void SetUpdateCancellationEnabled(bool enabled)
    {
        if (_trayCancelUpdate is null) return;
        _trayCancelUpdate.Visible = enabled;
        _trayCancelUpdate.Enabled = enabled;
    }

    void ResetWidgetPosition()
    {
        _settings.WidgetVisible = true;
        _settings.Save();
        if (!_widget.IsVisible) _widget.Show();
        _dockController?.DockToPrimary();
        _widget.Activate();
    }

    void ExitApp()
    {
        if (_updating)
        {
            CancelPendingUpdate();
            return;
        }
        ExitAppCore();
    }

    void ExitAfterUpdate() => ExitAppCore();

    async void ExitAppCore()
    {
        if (_exitStarted) return;
        _exitStarted = true;
        Log.Write("使用者選擇結束");
        if (_usageMonitor is not null)
            await _usageMonitor.DisposeAsync();
        if (_agentStateService is not null)
            await _agentStateService.DisposeAsync();
        DisposeChatGptService();
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activationSignal?.Dispose();
        _activationSignal = null;
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* another shutdown path already released it */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        _dockController?.Dispose();
        base.OnExit(e);
    }

    // ---------- providers and data flow ----------

    int BaseIntervalSec => Math.Max(30, _settings.RefreshIntervalSec);

    UsageSource ActiveSource => ToUsageSource(_widget.ActiveProvider);

    static UsageSource ToUsageSource(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Claude => UsageSource.Claude,
        UsageProviderKind.ChatGpt => UsageSource.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    ChatGptUsageService GetChatGptService()
    {
        lock (_chatGptServiceGate)
        {
            if (_chatGptService is not null) return _chatGptService;
            _chatGptServicePath = _settings.CodexExecutablePath;
            _chatGptService = new ChatGptUsageService(() => _settings.CodexExecutablePath);
            return _chatGptService;
        }
    }

    void DisposeChatGptService()
    {
        lock (_chatGptServiceGate)
        {
            _chatGptService?.Dispose();
            _chatGptService = null;
            _chatGptServicePath = null;
        }
    }

    void ActivateProvider(UsageProviderKind provider)
    {
        _widget.SetActiveProvider(provider);
        _settings.ActiveProvider = provider.StorageKey();
        _settings.Save();
        UpdateProviderChecks();
        var source = ActiveSource;
        UpdateStrip();
        _usageMonitor?.RefreshNow(source);
    }

    void OnUsageChanged(UsageSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted || _usageMonitor is null) return;
            UpdateTray();
            UpdateStrip();
        });
    }

    void OnTaskStateChanged(StatusBarState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted) return;
            _taskState = _agentStateService?.Current ?? state;
            UpdateStrip();
        });
    }

    void UpdateStrip()
    {
        if (_usageMonitor is null) return;
        _widget.UpdateState(_usageMonitor.Current, _taskState);
    }

    void ShowTrayContextMenu()
    {
        if (_tray.ContextMenuStrip is { } menu)
            menu.Show(WinForms.Cursor.Position);
    }

    void UpdateTray()
    {
        var snapshots = _usageMonitor?.Current;
        if (snapshots is null) return;

        var allWindows = snapshots.Values.SelectMany(snapshot => snapshot.Windows).ToArray();
        var principal = UsageSummary.Principal(allWindows);
        var pct = principal is null ? (int?)null : (int)Math.Round(principal.UsedPercent);
        if (pct != _lastTrayPct)
        {
            _lastTrayPct = pct;
            var old = _tray.Icon;
            _tray.Icon = TrayIconRenderer.Render(principal?.UsedPercent);
            old?.Dispose();
        }

        var lines = new[] { UsageSource.Claude, UsageSource.Codex }
            .Select(source =>
            {
                var name = source == UsageSource.Claude ? L10n.T("provider_claude") : L10n.T("provider_chatgpt");
                var snapshot = snapshots.GetValueOrDefault(source);
                var window = snapshot is null ? null : UsageSummary.Principal(snapshot.Windows);
                var remaining = window is null
                    ? L10n.T("tray_usage_unavailable")
                    : L10n.F("tray_usage_remaining", Math.Round(window.RemainingPercent)) +
                      (snapshot!.Health is UsageHealth.Stale or UsageHealth.SignedOut
                          ? L10n.T("tray_usage_stale")
                          : "");
                return $"{name}: {remaining}";
            });
        var tip = string.Join("\n", lines);
        var trimmed = tip.Length > 127 ? tip[..127] : tip;
        if (_tray.Text != trimmed) _tray.Text = trimmed;
    }

    Task SignInActiveProviderAsync(bool force) => _widget.ActiveProvider == UsageProviderKind.Claude
        ? SignInClaudeAsync(force)
        : SignInChatGptAsync(force);

    Task SignInClaudeAsync(bool force)
    {
        if (_loginWindowOpen) return Task.CompletedTask;
        if (!force && _claudeService.HasTokens)
        {
            _usageMonitor?.RefreshNow(UsageSource.Claude);
            return Task.CompletedTask;
        }

        _loginWindowOpen = true;
        try
        {
            var login = new LoginWindow();
            var ok = login.ShowDialog() == true && login.Result is not null;
            if (ok)
            {
                _claudeService.SetTokens(login.Result!);
                _usageMonitor?.RefreshNow(UsageSource.Claude);
            }
        }
        finally
        {
            _loginWindowOpen = false;
        }
        return Task.CompletedTask;
    }

    async Task SignInChatGptAsync(bool force)
    {
        if (_loginWindowOpen) return;
        _loginWindowOpen = true;
        try
        {
            var service = GetChatGptService();
            var accountType = await service.GetAccountTypeAsync();
            if (!force && accountType is "chatgpt" or "personalAccessToken")
            {
                _usageMonitor?.RefreshNow(UsageSource.Codex);
                return;
            }
            if (accountType == "apiKey")
            {
                var answer = MessageBox.Show(
                    L10n.T("chatgpt_login_replaces_api_key"),
                    L10n.T("provider_chatgpt"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            _widget.ShowNotice(L10n.T("chatgpt_login_waiting"));
            await service.LoginViaBrowserAsync();
            _usageMonitor?.RefreshNow(UsageSource.Codex);
        }
        catch (Exception ex)
        {
            Log.Write($"ChatGPT sign-in failed ({DiagnosticsService.ClassifyError(ex)})");
            if (_widget.ActiveProvider == UsageProviderKind.ChatGpt)
                _widget.ShowError(L10n.T("err_usage_unavailable"));
        }
        finally
        {
            _loginWindowOpen = false;
        }
    }
}
