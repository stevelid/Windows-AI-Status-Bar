using System.IO;
using System.Net.Http;
using System.Windows;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using ClaudeUsageWidget.Providers;
using StatusBar.Core.Claude;
using StatusBar.Core.Codex;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;
using StatusBar.Core.Projects;
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
    CodexTaskProvider? _codexTaskProvider;
    ClaudeCodeTaskProvider? _claudeTaskProvider;
    DockController? _dockController;
    DetailsPaneWindow? _detailsPane;
    TaskHistory _history = null!;
    TurnEndMonitor? _turnEnds;
    UsageRateHistory _usageRates = null!;
    TaskActivityLog _activity = null!;
    ProjectOverviewRepository _projectOverviewRepository = null!;
    ProjectOverviewDocument _projectOverviewDocument = new();
    readonly object _projectOverviewGate = new();
    readonly ProjectSummaryQueue _projectSummaryQueue = new(TimeProvider.System);
    readonly SemaphoreSlim _summaryQueueSignal = new(0);
    readonly CancellationTokenSource _summaryLifetime = new();
    CancellationTokenSource? _activeSummaryCancellation;
    readonly object _summaryRunGate = new();
    IProjectSummaryProvider? _projectSummaryProvider;
    HttpClient? _jevHttp;
    JevTurnEndClassifier? _jevClassifier;
    string? _jevKeyInUse;
    TaskHistory? _demoHistory;
    TaskHistory _activeHistory = null!;
    AttentionNotifier? _attentionNotifier;
    SystemEventsAdapter? _systemEvents;
    long? _detailsPaneClosedAtMs;
    ChatGptUsageService? _chatGptService;
    string? _chatGptServicePath;
    Settings _settings = null!;
    StatusStripWindow _widget = null!;
    TrayController _trayController = null!;
    StatusBarState _taskState = StatusBarState.Empty;
    int? _agentStateRetentionMinutes;
    string? _agentStateCodexHomeOverride;
    string? _agentStateClaudeHomeOverride;
    bool _agentStateClaudeHooksEnabled;
    bool _agentStateDemoMode;
    bool _loginWindowOpen;
    bool _demoTasksRequested;
    bool _exitStarted;
    Mutex? _singleInstanceMutex;
    EventWaitHandle? _activationSignal;
    RegisteredWaitHandle? _activationWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (ClaudeHookSink.IsInvocation(e.Args))
        {
            try
            {
                await ClaudeHookSink.HandleAsync(Console.OpenStandardInput());
            }
            catch
            {
                // Hook mode always exits successfully and never surfaces errors to Claude Code.
            }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        AppPaths.Initialize();

        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        Log.Write($"=== 啟動 v{AppBuild.Label} pid={Environment.ProcessId} args=[{string.Join(' ', e.Args)}] path={Environment.ProcessPath}");
        if (AppPaths.ResolutionNote.Length > 0)
            Log.Write($"資料路徑備援: {AppPaths.ResolutionNote} -> {AppPaths.DataDir}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject?.GetType().Name ?? "Unknown"}");
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
        _trayController.UpdateVisibility(stripVisible: true, _detailsPane?.IsVisible == true);
        _widget.Activate();
    }

    void StartupCore()
    {
        FinalMessageCapture.ProjectOverviewEnabled = true;
        _settings = Settings.Load();
        _history = new TaskHistory(TimeProvider.System, Path.Combine(AppPaths.DataDir, "history.json"), _settings.KeepHistory);
        _usageRates = new UsageRateHistory(TimeProvider.System, Path.Combine(AppPaths.DataDir, "usage-history.json"), _settings.SaveUsageHistory);
        _activity = new TaskActivityLog(TimeProvider.System, Path.Combine(AppPaths.DataDir, "activity-history.json"), _settings.SaveUsageHistory);
        _projectOverviewRepository = new ProjectOverviewRepository(AppPaths.DataDir);
        var projectLoad = _projectOverviewRepository.Load();
        _projectOverviewDocument = projectLoad.Document;
        if (projectLoad.DiagnosticCode is not null)
            Log.Write($"Project overview data: {projectLoad.DiagnosticCode}");
        _projectSummaryProvider = new ProjectSummaryFallbackProvider(
            new ClaudeCliProjectSummaryProvider(), new CodexCliProjectSummaryProvider(AppPaths.DataDir), TimeProvider.System);
        _ = Task.Run(ProjectSummaryQueueLoopAsync);
        _turnEnds = new TurnEndMonitor(
            () => _jevClassifier,
            Log.Write,
            TimeProvider.System,
            (info, judgment) => _ = ApplyTurnJudgmentAsync(info, _settings.JevAffectsState ? judgment : null),
            () => _settings.JevAffectsState);
        ApplyJevSettings();
        if (_settings.UseClaudeCodeHooks)
        {
            try
            {
                ClaudeHookSettingsInstaller.EnsureCurrentCommand(_settings);
            }
            catch (Exception exception)
            {
                Log.Write($"Claude hook registration failed: {exception.GetType().Name}");
            }
        }
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

        if (AppPaths.LaunchableExecutable is null)
        {
            // Development run through the dotnet host: leave the installed copy's shortcut alone.
            Log.Write("Running under the dotnet host; auto-start shortcut left unchanged.");
        }
        else if (!_settings.FirstRunDone)
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
        _dockController = new DockController(_widget, _settings);
        _widget.ContextMenuRequested += ShowTrayContextMenu;
        _widget.TogglePaneRequested += ToggleDetailsPane;

        UpdateService.CleanupOldBinary();
        UpdateService.CleanupStaleTemporaryDirectories();
        SetupTray();
        _attentionNotifier = new AttentionNotifier(_settings, _trayController);
        if (autoStartNotice is not null)
        {
            _trayController.ShowBalloonTip(
                8000,
                "Windows AI Status Bar",
                L10n.F("autostart_problem", autoStartNotice),
                WinForms.ToolTipIcon.Warning);
        }
        if (_settings.WidgetVisible) _widget.Show();
        _trayController.UpdateVisibility(_widget.IsVisible, paneVisible: false);

        _usageMonitor = new UsageMonitor(
            [
                new ClaudeUsageProvider(_claudeService),
                new CodexUsageProvider(GetChatGptService),
            ],
            TimeProvider.System,
            () => TimeSpan.FromSeconds(BaseIntervalSec),
            _usageRates);
        _usageMonitor.Changed += OnUsageChanged;
        UpdateStrip();
        UpdateTray();
        _usageMonitor.Start();

        ConfigureAgentTaskService();
        _systemEvents = new SystemEventsAdapter(
            () => _ = ReconcileTasksQuietlyAsync(),
            () => _usageMonitor?.RefreshNow());

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
                _trayController.ShowUpdateAvailable(_pendingUpdate.Latest.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Error("自動檢查更新失敗", ex);
        }
    }

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
            _dockController?.Redock();
            _trayController.UpdateVisibility(stripVisible: true, _detailsPane?.IsVisible == true);
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

    void SetupTray()
    {
        _trayController = new TrayController(
            _settings,
            new TrayActions(
                ToggleWidget,
                TogglePaneFromTray,
                () => _usageMonitor?.RefreshNow(),
                SignInProvider,
                ShowSettings,
                CopyDiagnostics,
                SaveDiagnosticBundle,
                () => _ = CheckForUpdatesAsync(interactive: true),
                CancelPendingUpdate,
                ExitApp));
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
                DiagnosticsService.InspectCodex(_settings.CodexExecutablePath),
                _codexTaskProvider?.Diagnostics,
                _settings.JevEnabled ? _turnEnds?.Stats : null);
            System.Windows.Clipboard.SetText(report);
            _trayController.ShowBalloonTip(
                3500,
                "Windows AI Status Bar",
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

    void SaveDiagnosticBundle()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".zip",
                FileName = $"WindowsAIStatusBar-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                Filter = L10n.T("diagnostics_zip_filter"),
                InitialDirectory = AppPaths.DataDir,
                OverwritePrompt = true,
                Title = L10n.T("diagnostics_save_title"),
            };
            if (dialog.ShowDialog() != true) return;

            var codexDiagnostics = _codexTaskProvider?.Diagnostics;
            var codex = DiagnosticsService.InspectCodex(_settings.CodexExecutablePath);
            var providers = _usageMonitor?.Current.Values
                .Select(snapshot => new DiagnosticUsageProvider(
                    snapshot.Source == UsageSource.Claude ? "Claude" : "ChatGPT",
                    snapshot.Health.ToString(),
                    snapshot.LastSuccess,
                    snapshot.StatusCode))
                ?? Enumerable.Empty<DiagnosticUsageProvider>();
            var report = DiagnosticReportBuilder.Build(
                (typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3),
                AppBuild.Label,
                providers,
                _agentStateService?.Current ?? _taskState,
                codexDiagnostics,
                codex.Source,
                codex.ExecutableName,
                codex.Version);
            DiagnosticBundleWriter.Write(
                dialog.FileName,
                report,
                Log.ReadDiagnosticTail(),
                DiagnosticReportBuilder.BuildFormatDrift(codexDiagnostics));
            _trayController.ShowBalloonTip(
                3500,
                "Windows AI Status Bar",
                L10n.T("diagnostics_saved"),
                WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Save diagnostic bundle failed", ex);
            MessageBox.Show(
                L10n.F("diagnostics_save_failed", ex.GetType().Name),
                L10n.T("diagnostics_save_title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    void ApplySettingsChanges()
    {
        _settings.Normalize();
        _widget.ApplyScale();
        _widget.ApplyAppearance();
        if (_detailsPane?.IsVisible == true) _detailsPane.ApplyAppearance();
        _dockController?.Redock();
        _history.SetPersistence(_settings.KeepHistory);
        _usageRates.SetPersistence(_settings.SaveUsageHistory);
        _activity.SetPersistence(_settings.SaveUsageHistory);
        ApplyJevSettings();
        ConfigureAgentTaskService();
        UpdateTray();

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

    void ShowWidget()
    {
        _settings.WidgetVisible = true;
        _settings.Save();
        if (!_widget.IsVisible) _widget.Show();
        _dockController?.Redock();
        _trayController.UpdateVisibility(stripVisible: true, _detailsPane?.IsVisible == true);
        _widget.Activate();
    }

    void ToggleWidget()
    {
        if (_widget.IsVisible) HideWidget();
        else ShowWidget();
    }

    void HideWidget()
    {
        _detailsPane?.Close();
        _settings.WidgetVisible = false;
        _settings.Save();
        _widget.Hide();
        _trayController.UpdateVisibility(stripVisible: false, paneVisible: false);
    }

    void SetUpdateCancellationEnabled(bool enabled) => _trayController.SetUpdateCancellationEnabled(enabled);

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
        _systemEvents?.Dispose();
        _turnEnds?.Dispose();
        _systemEvents = null;
        if (_usageMonitor is not null)
            await _usageMonitor.DisposeAsync();
        if (_agentStateService is not null)
        {
            _agentStateService.EnteredNeedsAttention -= OnEnteredNeedsAttention;
            _agentStateService.TaskFinished -= OnTaskFinished;
            await _agentStateService.DisposeAsync();
        }
        _detailsPane?.Close();
        DisposeChatGptService();
        _trayController.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _summaryLifetime.Cancel();
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

    void ConfigureAgentTaskService()
    {
        var demoMode = _demoTasksRequested || _settings.DemoTasks;
        var codexHomeOverride = demoMode ? null : _settings.CodexHomeOverride;
        var claudeHomeOverride = demoMode ? null : _settings.ClaudeCodeHomeOverride;
        var claudeHooksEnabled = !demoMode && _settings.UseClaudeCodeHooks;

        if (_agentStateService is not null &&
            _agentStateRetentionMinutes == _settings.RecentlyCompletedMinutes &&
            _agentStateDemoMode == demoMode &&
            (demoMode ||
                string.Equals(_agentStateCodexHomeOverride, codexHomeOverride, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_agentStateClaudeHomeOverride, claudeHomeOverride, StringComparison.OrdinalIgnoreCase) &&
                _agentStateClaudeHooksEnabled == claudeHooksEnabled))
        {
            return;
        }

        if (_agentStateService is not null)
        {
            _agentStateService.StateChanged -= OnTaskStateChanged;
            _agentStateService.EnteredNeedsAttention -= OnEnteredNeedsAttention;
            _agentStateService.TaskFinished -= OnTaskFinished;
            _ = _agentStateService.DisposeAsync();
        }

        if (_codexTaskProvider is not null)
        {
            _codexTaskProvider.WatcherOverflowed -= OnCodexWatcherOverflow;
            _codexTaskProvider.Trace -= OnCodexTrace;
        }
        _codexTaskProvider = null;
        if (_claudeTaskProvider is not null) _claudeTaskProvider.Trace -= OnClaudeTrace;
        _claudeTaskProvider = null;
        IAgentTaskProvider[] taskProviders;
        if (demoMode)
        {
            taskProviders =
            [
                new DemoTaskProvider(AgentProvider.Codex, TimeProvider.System),
                new DemoTaskProvider(AgentProvider.Claude, TimeProvider.System),
            ];
        }
        else
        {
            var codexHome = _settings.CodexHomeOverride;
            var claudeHome = _settings.ClaudeCodeHomeOverride;
            var claudeHookPath = claudeHooksEnabled
                ? Path.Combine(AppPaths.DataDir, "claude-hooks.jsonl")
                : null;
            // P6.1: each provider is recreated after an unexpected failure without touching the other.
            var codex = new SupervisedTaskProvider(AgentProvider.Codex, () => CreateCodexTaskProvider(codexHome), TimeProvider.System);
            var claude = new SupervisedTaskProvider(
                AgentProvider.Claude,
                () => CreateClaudeTaskProvider(claudeHome, claudeHookPath),
                TimeProvider.System);
            codex.Supervision += OnProviderSupervision;
            claude.Supervision += OnProviderSupervision;
            var cloudClient = new CodexCloudTaskClient(() => _settings.CodexExecutablePath, codexHome);
            var cloud = new CodexCloudTaskProvider(cloudClient.ReadPageAsync, TimeProvider.System);
            taskProviders = [new CombinedTaskProvider(AgentProvider.Codex, codex, cloud), claude];
        }

        var stateTime = TimeProvider.System;
        // Demo tasks get their own in-memory history, so they never mix with (or get saved into) the real one.
        _activeHistory = demoMode
            ? _demoHistory ??= new TaskHistory(stateTime, Path.Combine(AppPaths.DataDir, "history-demo.json"))
            : _history;
        _agentStateService = new AgentStateService(
            taskProviders,
            stateTime,
            new StateServiceOptions(
                TimeSpan.FromMinutes(_settings.RecentlyCompletedMinutes),
                TaskTimings.Default.UnknownVisibleFor),
            new DismissalStore(stateTime, Path.Combine(AppPaths.DataDir, "state.json")),
            new NotificationGate(stateTime, Path.Combine(AppPaths.DataDir, "state.json")),
            _activeHistory,
            demoMode ? null : _activity);
        _agentStateRetentionMinutes = _settings.RecentlyCompletedMinutes;
        _agentStateDemoMode = demoMode;
        _agentStateCodexHomeOverride = codexHomeOverride;
        _agentStateClaudeHomeOverride = claudeHomeOverride;
        _agentStateClaudeHooksEnabled = claudeHooksEnabled;
        // Providers start inside the constructor, so subscribe before reading the state and read it
        // once: an update landing in between was otherwise neither shown nor logged.
        _agentStateService.StateChanged += OnTaskStateChanged;
        _agentStateService.EnteredNeedsAttention += OnEnteredNeedsAttention;
        _agentStateService.TaskFinished += OnTaskFinished;
        var initialState = _agentStateService.Current;
        LogTaskChanges(StatusBarState.Empty, initialState);
        _taskState = initialState;
        PruneProjectOverview(initialState);
        UpdateStrip();
    }

    readonly Dictionary<UsageSource, string> _loggedUsageStatus = new();

    void OnUsageChanged(UsageSnapshot snapshot)
    {
        LogUsageStatusChange(snapshot);
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted || _usageMonitor is null) return;
            UpdateTray();
            UpdateStrip();
        });
    }

    // Logs provider availability transitions only (status code and exception type, never
    // messages or values), so a slow or failed start can be diagnosed from log.txt.
    void LogUsageStatusChange(UsageSnapshot snapshot)
    {
        var status = snapshot.ErrorType is null
            ? $"{snapshot.StatusCode}/{snapshot.Health}"
            : $"{snapshot.StatusCode}/{snapshot.Health}/{snapshot.ErrorType}";
        lock (_loggedUsageStatus)
        {
            if (_loggedUsageStatus.TryGetValue(snapshot.Source, out var previous) && previous == status) return;
            _loggedUsageStatus[snapshot.Source] = status;
        }
        Log.Write($"Usage {snapshot.Source}: {status}");
    }

    void OnTaskStateChanged(StatusBarState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted) return;
            var next = _agentStateService?.Current ?? state;
            LogTaskChanges(_taskState, next);
            _taskState = next;
            PruneProjectOverview(next);
            UpdateStrip();
        });
    }

    void PruneProjectOverview(StatusBarState state)
    {
        if (_projectOverviewRepository is null) return;
        var activeThreadIds = state.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        lock (_projectOverviewGate)
        {
            var pruned = ProjectOverviewDocumentRules.Prune(_projectOverviewDocument, TimeProvider.System.GetUtcNow(), activeThreadIds);
            if (_projectOverviewDocument.Projects.SequenceEqual(pruned.Projects) &&
                _projectOverviewDocument.Memberships.SequenceEqual(pruned.Memberships) &&
                _projectOverviewDocument.Updates.SequenceEqual(pruned.Updates) &&
                _projectOverviewDocument.Acknowledgements.SequenceEqual(pruned.Acknowledgements) &&
                _projectOverviewDocument.Coverage.SequenceEqual(pruned.Coverage) &&
                _projectOverviewDocument.ThreadReferences.SequenceEqual(pruned.ThreadReferences)) return;
            try
            {
                _projectOverviewRepository.Save(pruned);
                _projectOverviewDocument = pruned;
                _detailsPane?.UpdateProjectDocument(pruned);
            }
            catch (Exception exception) { Log.Write($"Project overview retention: {exception.GetType().Name}"); }
        }
    }

    void OnEnteredNeedsAttention(AgentTask task)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted) return;
            _attentionNotifier?.Notify(task);
        });
    }

    void OnTaskFinished(AgentTask task)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_exitStarted) return;
            _attentionNotifier?.NotifyFinished(task);
        });
    }

    // Content-free task transitions (short ids, states, counts); titles are never logged.
    static void LogTaskChanges(StatusBarState previous, StatusBarState next)
    {
        try
        {
            foreach (var line in StatusBar.Core.Diagnostics.TaskChangeLog.Describe(previous, next))
                Log.Write(line);
        }
        catch (Exception ex)
        {
            Log.Write($"Task change logging failed ({ex.GetType().Name})");
        }
    }

    CodexTaskProvider CreateCodexTaskProvider(string? codexHome)
    {
        if (_codexTaskProvider is not null)
        {
            _codexTaskProvider.WatcherOverflowed -= OnCodexWatcherOverflow;
            _codexTaskProvider.Trace -= OnCodexTrace;
        }
        var provider = new CodexTaskProvider(codexHome);
        provider.TurnEnded += OnTurnEnded;
        provider.ProjectSummaryTurnEnded += QueueProjectSummary;
        provider.WatcherOverflowed += OnCodexWatcherOverflow;
        provider.Trace += OnCodexTrace;
        _codexTaskProvider = provider;
        return provider;
    }

    // Called by the supervisor on start and after each restart, so the replacement keeps logging.
    ClaudeCodeTaskProvider CreateClaudeTaskProvider(string? claudeHome, string? hookPath)
    {
        if (_claudeTaskProvider is not null) _claudeTaskProvider.Trace -= OnClaudeTrace;
        var provider = new ClaudeCodeTaskProvider(claudeHome, hookPath);
        provider.Trace += OnClaudeTrace;
        provider.TurnEnded += OnTurnEnded;
        provider.ProjectSummaryTurnEnded += QueueProjectSummary;
        _claudeTaskProvider = provider;
        return provider;
    }

    async Task ReconcileTasksQuietlyAsync()
    {
        var service = _agentStateService;
        if (service is null) return;
        try
        {
            await service.ReconcileAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Task reconcile after system event failed", ex);
        }
    }

    static void OnProviderSupervision(string message) => Log.Write("Supervisor: " + message);

    void OnCodexWatcherOverflow() => Log.Write("Codex session watcher overflow; full reconciliation scheduled.");

    static void OnCodexTrace(string message) => Log.Write("Codex: " + message);

    static void OnClaudeTrace(string message) => Log.Write("Claude: " + message);

    void QueueProjectSummary(StatusBar.Core.Judgment.TurnEndInfo info)
    {
        if (_projectSummaryProvider is null || string.IsNullOrWhiteSpace(info.StableTaskId) ||
            string.IsNullOrWhiteSpace(info.EvidenceKey) || info.StableTaskId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
            info.EvidenceKey.Length > ProjectTextLimits.MaximumEvidenceIdLength) return;

        var task = _taskState.Tasks.FirstOrDefault(item => item.Id == info.StableTaskId);
        var title = ProjectSummaryTextSanitizer.Sanitize(info.DisplayTitle ?? task?.Title ?? "AI conversation", 120) ?? "AI conversation";
        var assistantText = (info.Start is null ? string.Empty : "[message start]\n" + info.Start + "\n[message end]\n") + info.Text;
        string[] candidateNames;
        string? priorSummary;
        lock (_projectOverviewGate)
        {
            candidateNames = _projectOverviewDocument.Projects.Select(project => project.Name)
                .Take(ProjectSummaryValidator.MaximumCandidateProjectNames).ToArray();
            var existingProjectIds = _projectOverviewDocument.Memberships
                .Where(item => item.ThreadId == info.StableTaskId)
                .Select(item => item.ProjectId).ToHashSet(StringComparer.Ordinal);
            priorSummary = _projectOverviewDocument.Projects
                .Where(project => existingProjectIds.Contains(project.Id))
                .Select(project => project.CurrentSummary).FirstOrDefault(summary => !string.IsNullOrWhiteSpace(summary));
        }
        var request = new ProjectSummaryRequest(
            info.StableTaskId,
            info.EvidenceKey,
            title,
            ProjectSummaryTextSanitizer.Sanitize(info.ContextText, 1200),
            ProjectSummaryTextSanitizer.Sanitize(assistantText, 1800) ?? string.Empty,
            candidateNames,
            ProjectSummaryTextSanitizer.Sanitize(priorSummary, 1000),
            OccurredAt: info.OccurredAt ?? DateTimeOffset.UtcNow,
            EarlierEvidence: []);
        if (!ProjectSummaryValidator.IsValidRequest(request)) return;

        lock (_projectOverviewGate)
        {
            if (_projectOverviewDocument.Coverage.Any(item => item.ThreadId == request.ThreadId && item.TurnId == request.EvidenceId &&
                (item.ProjectId is not null || item.PromptVersion == "project-overview-v2"))) return;
            if (_projectSummaryQueue.Enqueue(request)) _summaryQueueSignal.Release();
        }
    }

    async Task ProjectSummaryQueueLoopAsync()
    {
        while (!_summaryLifetime.IsCancellationRequested)
        {
            try
            {
                await _summaryQueueSignal.WaitAsync(_summaryLifetime.Token);
                await Task.Delay(TimeSpan.FromSeconds(35), _summaryLifetime.Token);
                if (!_settings.ProjectSummariesEnabled) continue;
                while (_projectSummaryQueue.TryDequeue(out var work))
                {
                    if (!_settings.ProjectSummariesEnabled)
                    {
                        _projectSummaryQueue.Requeue(work);
                        break;
                    }
                    if (!await WaitForSummaryBudgetAsync(_summaryLifetime.Token))
                    {
                        _projectSummaryQueue.Requeue(work);
                        break;
                    }
                    if (!_settings.ProjectSummariesEnabled)
                    {
                        _projectSummaryQueue.Requeue(work);
                        break;
                    }
                    using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_summaryLifetime.Token);
                    try
                    {
                        var provider = _projectSummaryProvider;
                        if (provider is null) break;
                        lock (_summaryRunGate)
                        {
                            if (!_settings.ProjectSummariesEnabled)
                            {
                                _projectSummaryQueue.Requeue(work);
                                break;
                            }
                            _activeSummaryCancellation = launchCancellation;
                        }
                        _projectSummaryQueue.RecordLaunch();
                        var result = await provider.SummarizeAsync(work.Request, launchCancellation.Token);
                        if (!_settings.ProjectSummariesEnabled)
                        {
                            _projectSummaryQueue.Requeue(work);
                            break;
                        }
                        if (result is not null && ProjectSummaryValidator.TryValidate(work.Request, result))
                        {
                            if (_projectSummaryQueue.IsCurrent(work))
                            {
                                _projectSummaryQueue.RecordSuccess();
                                _detailsPane?.UpdateProjectSummaryStatus(true);
                                CommitProjectSummary(work, result);
                            }
                        }
                        else
                        {
                            _projectSummaryQueue.RecordFailure();
                            _detailsPane?.UpdateProjectSummaryStatus(true, unavailable: true);
                        }
                    }
                    catch (OperationCanceledException) when (_summaryLifetime.IsCancellationRequested) { return; }
                    catch (OperationCanceledException) when (!_settings.ProjectSummariesEnabled)
                    {
                        _projectSummaryQueue.Requeue(work);
                        break;
                    }
                    catch (Exception exception)
                    {
                        // Log only a fixed failure category; never include worker text, paths or raw diagnostics.
                        _projectSummaryQueue.RecordFailure();
                        _detailsPane?.UpdateProjectSummaryStatus(true, unavailable: true);
                        Log.Write($"Project summary worker: {(exception is ProjectSummaryWorkerException workerFailure ? workerFailure.Code : exception.GetType().Name)}");
                    }
                    finally
                    {
                        lock (_summaryRunGate)
                            if (ReferenceEquals(_activeSummaryCancellation, launchCancellation)) _activeSummaryCancellation = null;
                        _projectSummaryQueue.Complete(work);
                    }
                }
            }
            catch (OperationCanceledException) when (_summaryLifetime.IsCancellationRequested) { return; }
            catch (Exception exception) { Log.Write($"Project summary queue: {exception.GetType().Name}"); }
        }
    }

    async Task<bool> WaitForSummaryBudgetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!_settings.ProjectSummariesEnabled) return false;
            var delay = _projectSummaryQueue.GetLaunchDelay();
            if (delay <= TimeSpan.Zero) return true;
            var signaled = await _summaryQueueSignal.WaitAsync(delay, cancellationToken);
            if (signaled && !_settings.ProjectSummariesEnabled) return false;
        }
    }

    void CommitProjectSummary(ProjectSummaryWorkItem work, ProjectSummaryResult result)
    {
        ProjectOverviewDocument updated;
        lock (_projectOverviewGate)
        {
            if (!_projectSummaryQueue.IsCurrent(work)) return;
            updated = ProjectSummaryReducer.Apply(_projectOverviewDocument, work.Request, result, TimeProvider.System);
            _projectOverviewRepository.Save(updated);
            _projectOverviewDocument = updated;
        }
        Dispatcher.BeginInvoke(() => _detailsPane?.UpdateProjectDocument(updated));
    }

    void OnTurnEnded(TurnEndInfo info)
    {
        _turnEnds?.Observe(info);
        // Log-only mode: the built-in rules apply at once instead of waiting for the answer.
        if (!_settings.JevAffectsState) _ = ApplyTurnJudgmentAsync(info, null);
    }

    // Returns the AI's answer (null: rules decide) to the provider that owns the finished turn.
    async Task ApplyTurnJudgmentAsync(TurnEndInfo info, TurnEndJudgment? judgment)
    {
        try
        {
            if (info.Provider == AgentProvider.Codex && _codexTaskProvider is { } codex)
                await codex.ApplyTurnJudgmentAsync(info, judgment);
            else if (info.Provider == AgentProvider.Claude && _claudeTaskProvider is { } claude)
                await claude.ApplyTurnJudgmentAsync(info, judgment);
        }
        catch (Exception ex)
        {
            Log.Error("Applying a turn judgment failed", ex);
        }
    }

    // The AI check is opt-in and needs a saved key. Final-message text is only kept in memory while it is on.
    void ApplyJevSettings()
    {
        var key = _settings.JevEnabled ? JevKeyStore.Load() : null;
        if (!string.Equals(key, _jevKeyInUse, StringComparison.Ordinal))
        {
            _jevKeyInUse = key;
            _jevClassifier = key is null
                ? null
                : new JevTurnEndClassifier(_jevHttp ??= new HttpClient { Timeout = TimeSpan.FromSeconds(8) }, key);
        }
        FinalMessageCapture.Enabled = _jevClassifier is not null;
    }

    void OnDismissTaskRequested(string taskId) => _agentStateService?.Dismiss(taskId);

    void OnProjectUpdateOpened(ProjectAcknowledgement acknowledgement)
    {
        lock (_projectOverviewGate)
        {
            if (!_projectOverviewDocument.Updates.Any(update => update.Id == acknowledgement.UpdateId && update.Revision == acknowledgement.Revision)) return;
            var acknowledgements = _projectOverviewDocument.Acknowledgements
                .Where(item => !(item.UpdateId == acknowledgement.UpdateId && item.Revision == acknowledgement.Revision))
                .Append(acknowledgement).ToList();
            var updated = _projectOverviewDocument with { Acknowledgements = acknowledgements };
            try
            {
                _projectOverviewRepository.Save(updated);
                _projectOverviewDocument = updated;
                _detailsPane?.UpdateProjectDocument(updated);
            }
            catch (Exception exception) { Log.Write($"Project overview save: {exception.GetType().Name}"); }
        }
    }

    async void OnProjectUpdateOpenRequested(ProjectAcknowledgement acknowledgement, AgentTask task)
    {
        var result = await AppActivator.TryFocusAsync(task);
        Log.Write($"Focus {task.Provider} ({task.Status}): {result switch
        {
            AppActivator.FocusResult.SessionLinkDispatched => "session link dispatched",
            AppActivator.FocusResult.AppFocused => "app activated (session link unavailable)",
            _ => "activation failed",
        }}");
        if (result == AppActivator.FocusResult.Failed) return;
        OnProjectUpdateOpened(acknowledgement);
        _detailsPane?.Close();
    }

    void OnProjectAssignmentRequested(string threadId, string? projectId)
    {
        lock (_projectOverviewGate)
        {
            if (projectId is not null && !_projectOverviewDocument.Projects.Any(project => project.Id == projectId)) return;
            // Keep prior associations so their summaries and source links remain valid history;
            // the one current manual choice takes precedence in the Core projection.
            var memberships = _projectOverviewDocument.Memberships.Select(item =>
                item.ThreadId == threadId && item.Source == ProjectMembershipSource.Manual && !item.IsUnassigned
                    ? item with { Source = ProjectMembershipSource.Suggested }
                    : item).ToList();
            memberships.RemoveAll(item => item.ThreadId == threadId && item.IsUnassigned);
            if (projectId is not null)
            {
                memberships.RemoveAll(item => item.ThreadId == threadId && item.ProjectId == projectId && item.Source != ProjectMembershipSource.Manual);
                memberships.Add(new ProjectMembership(projectId, threadId, ProjectMembershipSource.Manual));
            }
            else memberships.Add(new ProjectMembership(string.Empty, threadId, ProjectMembershipSource.Manual, IsUnassigned: true));
            var updated = _projectOverviewDocument with
            {
                Memberships = memberships,
            };
            SaveProjectDocument(updated);
        }
    }

    void OnProjectDeleteRequested(string projectId)
    {
        lock (_projectOverviewGate)
        {
            try
            {
                InvalidateProjectSummaryWork();
                var updated = ProjectOverviewDocumentRules.DeleteProject(_projectOverviewDocument, projectId);
                _projectOverviewRepository.DeleteProject(updated, projectId);
                _projectOverviewDocument = updated;
                _detailsPane?.UpdateProjectDocument(updated);
            }
            catch (Exception exception) { Log.Write($"Project overview delete: {exception.GetType().Name}"); }
        }
    }

    void OnProjectOverviewClearRequested()
    {
        lock (_projectOverviewGate)
        {
            try
            {
                InvalidateProjectSummaryWork();
                _projectOverviewRepository.Clear();
                _projectOverviewDocument = ProjectOverviewDocumentRules.Clear();
                _detailsPane?.UpdateProjectDocument(_projectOverviewDocument);
            }
            catch (Exception exception) { Log.Write($"Project overview clear: {exception.GetType().Name}"); }
        }
    }

    void OnProjectSummaryToggleRequested()
    {
        lock (_summaryRunGate)
        {
            _settings.ProjectSummariesEnabled = !_settings.ProjectSummariesEnabled;
            if (!_settings.ProjectSummariesEnabled) _activeSummaryCancellation?.Cancel();
        }
        _settings.Save();
        if (_settings.ProjectSummariesEnabled)
        {
            _summaryQueueSignal.Release();
            _detailsPane?.UpdateProjectSummaryStatus(true);
        }
        else
        {
            _detailsPane?.UpdateProjectSummaryStatus(false);
        }
    }

    void InvalidateProjectSummaryWork()
    {
        _projectSummaryQueue.InvalidateAll();
        lock (_summaryRunGate) _activeSummaryCancellation?.Cancel();
    }

    void SaveProjectDocument(ProjectOverviewDocument updated)
    {
        try
        {
            if (!ProjectOverviewDocumentValidator.TryValidate(updated, out var error)) throw new InvalidDataException(error);
            _projectOverviewRepository.Save(updated);
            _projectOverviewDocument = updated;
            _detailsPane?.UpdateProjectDocument(updated);
        }
        catch (Exception exception) { Log.Write($"Project overview save: {exception.GetType().Name}"); }
    }

    async void OnFocusTaskRequested(AgentTask task)
    {
        var result = await AppActivator.TryFocusAsync(task);
        // Content-free: provider and outcome only, so "clicking did nothing" can be diagnosed.
        Log.Write($"Focus {task.Provider} ({task.Status}): {result switch
        {
            AppActivator.FocusResult.SessionLinkDispatched => "session link dispatched",
            AppActivator.FocusResult.AppFocused => "app activated (session link unavailable)",
            _ => "activation failed",
        }}");
        if (result != AppActivator.FocusResult.Failed)
            _detailsPane?.Close();
    }

    void UpdateStrip()
    {
        if (_usageMonitor is null) return;
        var usage = _usageMonitor.Current;
        _widget.UpdateState(usage, _taskState);
        _trayController.UpdateTaskState(_taskState);
        if (_detailsPane?.IsVisible == true)
            _detailsPane.UpdateState(usage, _taskState);
    }

    // Clicking the strip activates it, which deactivates (and so closes) the open pane on
    // mouse-down; the strip's toggle then fires on mouse-up. Treat a toggle that arrives just
    // after a close as that same click, otherwise "click to close" would reopen the pane.
    const long PaneReopenGuardMs = 400;

    void ToggleDetailsPane()
    {
        if (_detailsPane is { IsVisible: true })
        {
            _detailsPane.Close();
            _trayController.UpdateVisibility(_widget.IsVisible, paneVisible: false);
            return;
        }

        // Null until the pane has closed once. (A long.MinValue sentinel overflowed the
        // subtraction and blocked every open.)
        if (_detailsPaneClosedAtMs is long closedAt &&
            Environment.TickCount64 - closedAt < PaneReopenGuardMs) return;
        if (!_widget.IsVisible || _usageMonitor is null) return;
        _detailsPane = new DetailsPaneWindow(_settings, _activeHistory, _usageRates, _activity, _projectOverviewDocument);
        _detailsPane.ClearFinishedRequested += () => _agentStateService?.DismissFinished();
        _detailsPane.OpenHistoryTaskRequested += OnFocusTaskRequested;
        _detailsPane.DismissTaskRequested += OnDismissTaskRequested;
        _detailsPane.FocusTaskRequested += OnFocusTaskRequested;
        _detailsPane.ProjectUpdateOpenRequested += OnProjectUpdateOpenRequested;
        _detailsPane.ProjectAssignmentRequested += OnProjectAssignmentRequested;
        _detailsPane.ProjectDeleteRequested += OnProjectDeleteRequested;
        _detailsPane.ClearProjectOverviewRequested += OnProjectOverviewClearRequested;
        _detailsPane.ProjectSummaryToggleRequested += OnProjectSummaryToggleRequested;
        _detailsPane.Closed += (_, _) =>
        {
            _detailsPaneClosedAtMs = Environment.TickCount64;
            _widget.SetPaneOpen(false);
            _trayController.UpdateVisibility(_widget.IsVisible, paneVisible: false);
        };
        _detailsPane.UpdateState(_usageMonitor.Current, _taskState);
        _detailsPane.ShowAbove(_widget);
        _widget.SetPaneOpen(true);
        _trayController.UpdateVisibility(_widget.IsVisible, paneVisible: true);
    }

    void TogglePaneFromTray()
    {
        if (!_widget.IsVisible) ShowWidget();
        ToggleDetailsPane();
    }

    void ShowTrayContextMenu() => _trayController.ShowContextMenuAtCursor();

    void UpdateTray()
    {
        var snapshots = _usageMonitor?.Current;
        if (snapshots is not null) _trayController.UpdateUsage(snapshots);
    }

    void SignInProvider(UsageProviderKind provider)
    {
        if (provider == UsageProviderKind.Claude)
            _ = SignInClaudeAsync(force: true);
        else
            _ = SignInChatGptAsync(force: true);
    }

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
            _widget.ShowError(L10n.T("err_usage_unavailable"));
        }
        finally
        {
            _loginWindowOpen = false;
        }
    }
}
