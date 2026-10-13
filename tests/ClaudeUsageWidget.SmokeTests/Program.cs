using ClaudeUsageWidget;
using StatusBar.Core.Codex;
using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;
using StatusBar.Core.Projects;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

L10n.Init(UiLanguage.En);
var finalSummaryOutput = System.Text.Json.JsonSerializer.Serialize(new
{
    type = "turn.completed",
    last_agent_message = System.Text.Json.JsonSerializer.Serialize(new
    {
        suggestedProjectName = "Sample project", latestAction = "Prepared a sample", progress = Array.Empty<string>(),
        sourceEvidenceIds = new[] { "sample-turn:assistant" },
    }),
});
Require(CodexCliProjectSummaryProvider.ReadFinalResult(finalSummaryOutput)?.LatestAction == "Prepared a sample",
    "The summary worker must parse only the final assistant result.");
Require(CodexCliProjectSummaryProvider.ReadFinalResult("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"not final\"}}") is null,
    "An intermediate assistant item must not be accepted as the final summary.");
Require(CodexCliProjectSummaryProvider.ReadFinalResult("{\"type\":17}") is null,
    "Malformed event types must fail closed.");

// The strip shows the session window, identified by the length the adapters attach.
Require(
    ClaudeUsageWidget.Providers.ClaudeUsageProvider.WindowLength("session") == TimeSpan.FromHours(5) &&
    ClaudeUsageWidget.Providers.ClaudeUsageProvider.WindowLength("five_hour") == TimeSpan.FromHours(5) &&
    ClaudeUsageWidget.Providers.ClaudeUsageProvider.WindowLength("weekly_all") == TimeSpan.FromDays(7) &&
    ClaudeUsageWidget.Providers.ClaudeUsageProvider.WindowLength("unknown_kind") is null,
    "Claude usage keys should map to session and weekly window lengths.");
Require(
    ClaudeUsageWidget.Providers.CodexUsageProvider.WindowLength("chatgpt_0_300") == TimeSpan.FromMinutes(300) &&
    ClaudeUsageWidget.Providers.CodexUsageProvider.WindowLength("chatgpt_1_10080") == TimeSpan.FromDays(7) &&
    ClaudeUsageWidget.Providers.CodexUsageProvider.WindowLength("chatgpt_0_0") is null,
    "Codex usage keys should carry the window length in minutes.");

const string expiredRefreshTokenResponse = """
{
  "error": "invalid_grant",
  "error_description": "Refresh token expired"
}
""";
var expiredRefreshTokenError = OAuthTokenRequestException.FromResponse(
    System.Net.HttpStatusCode.BadRequest,
    expiredRefreshTokenResponse);
Require(expiredRefreshTokenError.IsInvalidGrant, "An expired OAuth refresh token should require sign-in again.");
Require(
    !expiredRefreshTokenError.Message.Contains("Refresh token expired") &&
    !expiredRefreshTokenError.Message.Contains("error_description"),
    "OAuth token endpoint response details should not be exposed in exception messages.");
// The sign-in hints must name the real menu path (built from the menu's own labels), so a
// renamed menu item cannot leave the hint pointing at something that no longer exists.
static string SignInPath(string providerKey) => $"{L10n.T("menu_sign_in")} › {L10n.T(providerKey)}";
Require(
    L10n.T("err_token_expired").Contains(SignInPath("provider_claude")) &&
    L10n.T("err_token_expired").Contains("tray icon"),
    "The expired Claude sign-in message should tell users where to sign in again.");
Require(
    L10n.T("err_chatgpt_not_signed_in").Contains(SignInPath("menu_sign_in_codex")),
    "The ChatGPT sign-in message should name the Sign in › ChatGPT / Codex menu item.");
L10n.Init(UiLanguage.ZhHant);
Require(
    L10n.T("err_token_expired").Contains(SignInPath("provider_claude")) &&
    L10n.T("err_token_expired").Contains("系統匣圖示"),
    "The Traditional Chinese expired Claude sign-in message should include re-login steps.");
L10n.Init(UiLanguage.En);

const string updateZip = "AIStatusBar-win-x64.zip";
const string expectedUpdateHash = "e68928a80d0cf0ba34c93c249794587ac833617bcdbbe2034bd61db23262e0e9";
Require(
    UpdateService.ParseExpectedHash($"{expectedUpdateHash}  {updateZip}\n", updateZip) == expectedUpdateHash,
    "The release checksum parser should select the expected zip asset.");
var missingChecksumWasRejected = false;
try
{
    UpdateService.ParseExpectedHash($"{expectedUpdateHash}  another-file.zip\n", updateZip);
}
catch (InvalidOperationException) { missingChecksumWasRejected = true; }
Require(missingChecksumWasRejected, "A missing update checksum should be rejected.");

var updaterTestRoot = Path.Combine(
    Path.GetTempPath(),
    "ClaudeUsageWidget.UpdaterTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(updaterTestRoot);
try
{
    var downloadedFile = Path.Combine(updaterTestRoot, "download.zip");
    await File.WriteAllTextAsync(downloadedFile, "known test content");
    var hashMismatchWasRejected = false;
    try
    {
        await UpdateService.VerifySha256Async(downloadedFile, new string('0', 64));
    }
    catch (InvalidOperationException) { hashMismatchWasRejected = true; }
    Require(hashMismatchWasRejected, "A downloaded update with the wrong SHA-256 should be rejected.");

    var cancellationWasObserved = false;
    using (var input = new MemoryStream(new byte[128]))
    using (var output = new MemoryStream())
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try
        {
            await UpdateService.CopyDownloadAsync(
                input,
                output,
                input.Length,
                progress: null,
                cancellation.Token);
        }
        catch (OperationCanceledException) { cancellationWasObserved = true; }
    }
    Require(cancellationWasObserved, "Cancelling an update download should stop before writing.");

    var currentExe = Path.Combine(updaterTestRoot, "current.exe");
    File.WriteAllText(currentExe, "original executable");
    var replacementFailureWasObserved = false;
    try
    {
        UpdateService.ReplaceExecutable(
            currentExe,
            Path.Combine(updaterTestRoot, "missing-replacement.exe"));
    }
    catch (IOException) { replacementFailureWasObserved = true; }
    Require(replacementFailureWasObserved, "The replacement failure test should exercise rollback.");
    Require(File.Exists(currentExe), "The original executable should be restored after replacement failure.");
    Require(
        File.ReadAllText(currentExe) == "original executable",
        "Rollback should preserve the original executable contents.");
    Require(!File.Exists(currentExe + ".old"), "Rollback should not strand the original as an .old file.");

    var abandonedUpdate = Path.Combine(
        updaterTestRoot,
        "WindowsAIStatusBar-update-" + Guid.NewGuid().ToString("N"));
    var recentUpdate = Path.Combine(
        updaterTestRoot,
        "WindowsAIStatusBar-update-" + Guid.NewGuid().ToString("N"));
    var unrelatedDirectory = Path.Combine(updaterTestRoot, "unrelated-temp-data");
    Directory.CreateDirectory(abandonedUpdate);
    Directory.CreateDirectory(recentUpdate);
    Directory.CreateDirectory(unrelatedDirectory);
    File.WriteAllText(Path.Combine(abandonedUpdate, "payload.bin"), "old");
    Directory.SetLastWriteTimeUtc(abandonedUpdate, DateTime.UtcNow.AddDays(-10));

    UpdateService.CleanupStaleTemporaryDirectories(
        updaterTestRoot,
        DateTime.UtcNow,
        TimeSpan.FromDays(7));
    Require(!Directory.Exists(abandonedUpdate), "Abandoned updater data should be removed.");
    Require(Directory.Exists(recentUpdate), "Recent updater data should be preserved.");
    Require(Directory.Exists(unrelatedDirectory), "Unrelated temporary data should never be removed.");
    Require(
        !UpdateService.TryDeleteTemporaryDirectory(unrelatedDirectory, updaterTestRoot),
        "The updater cleanup guard should reject unrelated directory names.");
}
finally
{
    if (Directory.Exists(updaterTestRoot)) Directory.Delete(updaterTestRoot, recursive: true);
}

var privateError = new IOException(@"secret-token at C:\Users\Alice\private");
var diagnosticReport = DiagnosticsService.BuildReport(
    new Version(2, 0, 4),
    new[]
    {
        new ProviderDiagnostic(
            UsageSource.Claude,
            UsageHealth.Unavailable,
            DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
            DiagnosticsService.ClassifyError(privateError)),
        new ProviderDiagnostic(UsageSource.Codex, UsageHealth.Loading, null, "NotChecked"),
    },
    new CodexDiagnostic("Configured", "codex.exe", "1.2.3"),
    new CodexTaskDiagnostics(
        new ProviderHealth(ProviderHealthState.Ok, "Ok", DateTimeOffset.Parse("2030-01-01T00:00:00Z")),
        SessionsTracked: 2,
        FilesWatched: 2,
        LastEventAge: TimeSpan.FromSeconds(7),
        ParseErrors: 3,
        FormatDriftCount: 2,
        FormatDriftBySignature: new Dictionary<string, long>
        {
            ["event_msg/future_event"] = 2,
            [@"C:\Users\Alice/secret"] = 1,
        },
        WatcherOverflowCount: 1));
Require(!diagnosticReport.Contains("secret-token"), "Diagnostics should not include exception messages.");
Require(!diagnosticReport.Contains(@"C:\Users\Alice"), "Diagnostics should not include full user paths.");
Require(diagnosticReport.Contains("CodexTasks.SessionsTracked: 2"), "Diagnostics should report tracked sessions.");
Require(diagnosticReport.Contains("CodexTasks.FilesWatched: 2"), "Diagnostics should report watched files.");
Require(diagnosticReport.Contains("CodexTasks.LastEventAge: 7s"), "Diagnostics should report the age of recent Codex evidence.");
Require(diagnosticReport.Contains("CodexTasks.FormatDrift.event_msg/future_event: 2"), "Diagnostics should report drift signatures by structural type names.");
Require(!AutoStart.SafeFailureCode(privateError).Contains("secret-token"), "Auto-start failure codes should be redacted.");

var primaryWorkArea = new StatusBar.Core.Docking.Rect(0, 0, 1920, 1040);
var recoveredStrip = StatusBar.Core.Docking.DockGeometry.Place(
    primaryWorkArea,
    new StatusBar.Core.Docking.Size(320, 30),
    StatusBar.Core.Docking.DockAnchor.BottomRight,
    12);
Require(
    recoveredStrip.Right <= primaryWorkArea.Right && recoveredStrip.Bottom <= primaryWorkArea.Bottom,
    "The compact strip should stay inside the primary work area.");

const string claudePayload = """
{
  "limits": [
    { "kind": "session", "percent": 12.5, "resets_at": "2030-01-01T00:00:00Z" },
    { "kind": "weekly_all", "percent": 34.0, "resets_at": "2030-01-02T00:00:00Z" },
    { "kind": "weekly_scoped", "percent": 56.0, "scope": { "model": { "display_name": "Opus" } } }
  ]
}
""";
var claude = UsageParser.Parse(claudePayload);
Require(claude.Count == 3, "Claude limits array should produce three rows.");
Require(claude[2].Label == "Weekly (Opus)", "Claude scoped model label wasn't preserved.");

var projectStorePath = Path.Combine(Path.GetTempPath(), "WindowsAIStatusBar.ProjectStore", Guid.NewGuid().ToString("N"));
var projectStore = new ProjectOverviewRepository(projectStorePath);
var projectDocument = new ProjectOverviewDocument
{
    Projects = [new ProjectDefinition { Id = "fake-project", Name = "Fictional project", CurrentSummary = "Prepared the sample" }],
    Memberships = [new ProjectMembership("fake-project", "codex:fake-thread", ProjectMembershipSource.Manual)],
    Updates = [new ProjectUpdate { Id = "fake-update", ProjectId = "fake-project", ThreadId = "codex:fake-thread", Revision = 1, OccurredAt = DateTimeOffset.UtcNow, LatestAction = "Prepared fictional sample" }],
    Coverage = [new ProjectCoverageCheckpoint("fake-project", "codex:fake-thread", "fake-turn", DateTimeOffset.UtcNow, "1", "test", "test-model")],
};
try
{
    projectStore.Save(projectDocument);
    var firstCiphertext = File.ReadAllBytes(Path.Combine(projectStorePath, "projects.dat"));
    Require(!System.Text.Encoding.UTF8.GetString(firstCiphertext).Contains("Prepared the sample", StringComparison.Ordinal),
        "The project store must not expose summary text in plaintext.");
    projectStore.Save(projectDocument with { Projects = [projectDocument.Projects[0] with { CurrentSummary = "Revised fictional sample" }] });
    var primaryFile = Path.Combine(projectStorePath, "projects.dat");
    var primaryBytes = File.ReadAllBytes(primaryFile);
    primaryBytes[0] ^= 0xFF;
    File.WriteAllBytes(primaryFile, primaryBytes);
    var recovered = projectStore.Load();
    Require(recovered.Source == ProjectOverviewLoadSource.Recovery && recovered.Document.Projects[0].CurrentSummary == "Prepared the sample",
        "The encrypted recovery snapshot should load when the primary is damaged.");
    projectStore.Save(recovered.Document);
    Require(File.Exists(Path.Combine(projectStorePath, "projects.unreadable.dat")),
        "Unreadable primary bytes should be preserved in encrypted quarantine before replacement.");
    projectStore.Clear();
    Require(!Directory.EnumerateFiles(projectStorePath).Any(), "Clear should purge primary, recovery and quarantine files.");
}
finally
{
    if (Directory.Exists(projectStorePath)) Directory.Delete(projectStorePath, recursive: true);
}

var fakeLocalAppData = Path.Combine(Path.GetTempPath(), "ClaudeUsageWidget.SmokeTests", Guid.NewGuid().ToString("N"));
try
{
    var oldDirectory = Path.Combine(fakeLocalAppData, "OpenAI", "Codex", "bin", "old-version");
    var currentDirectory = Path.Combine(fakeLocalAppData, "OpenAI", "Codex", "bin", "current-version");
    Directory.CreateDirectory(oldDirectory);
    Directory.CreateDirectory(currentDirectory);
    var oldCodex = Path.Combine(oldDirectory, "codex.exe");
    var currentCodex = Path.Combine(currentDirectory, "codex.exe");
    File.WriteAllText(oldCodex, "old");
    File.WriteAllText(currentCodex, "current");
    File.SetLastWriteTimeUtc(oldCodex, DateTime.UtcNow.AddMinutes(-5));
    File.SetLastWriteTimeUtc(currentCodex, DateTime.UtcNow);

    var discovered = CodexLocator.FindChatGptDesktopCodex(fakeLocalAppData);
    Require(
        string.Equals(discovered, currentCodex, StringComparison.OrdinalIgnoreCase),
        "ChatGPT desktop Codex discovery should select the newest installed candidate.");
}
finally
{
    if (Directory.Exists(fakeLocalAppData)) Directory.Delete(fakeLocalAppData, recursive: true);
}

if (args.Length == 1 && string.Equals(args[0], "--live", StringComparison.OrdinalIgnoreCase))
{
    using var liveService = new ChatGptUsageService(() => null);
    var accountType = await liveService.GetAccountTypeAsync();
    if (accountType is null)
    {
        Console.WriteLine("Live ChatGPT desktop Codex probe passed (app-server started; separate Codex CLI sign-in required).");
        return;
    }

    var liveUsage = await liveService.GetUsageAsync();
    Require(liveUsage.Count > 0, "The live Codex app-server returned no displayable quota windows.");
    Console.WriteLine($"Live ChatGPT desktop Codex probe passed ({liveUsage.Count} quota windows; values withheld).");
    return;
}

var mockPath = args.FirstOrDefault()
    ?? throw new ArgumentException("Pass the CodexAppServerMock executable path as the first argument.");
using var service = new ChatGptUsageService(() => mockPath);
var chatGpt = await service.GetUsageAsync();
Require(chatGpt.Count == 2, "ChatGPT rate limits should produce two rows.");
Require(chatGpt[0].Label == "5-hour limit", "Primary ChatGPT window label is incorrect.");
Require(chatGpt[0].Utilization == 25.0, "Primary ChatGPT utilization is incorrect.");
Require(chatGpt[1].Label == "Weekly limit", "Secondary ChatGPT window label is incorrect.");
Require(chatGpt[1].Utilization == 40.0, "Secondary ChatGPT utilization is incorrect.");

Console.WriteLine("Smoke tests passed: sanitized OAuth expiry guidance, safe updater failure handling and cleanup, redacted diagnostics, window placement recovery, Claude parser, ChatGPT desktop Codex discovery, Codex JSON-RPC handshake, ChatGPT rate-limit parser.");
