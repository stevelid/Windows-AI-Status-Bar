using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeUsageWidget;
using StatusBar.Core.Tasks;
using StatusBar.Core.Projects;
using StatusBar.Core.Usage;

namespace WidgetVisualHarness;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var language = args.Contains(
            "--english",
            StringComparer.OrdinalIgnoreCase)
            ? UiLanguage.En
            : UiLanguage.ZhHant;
        L10n.Init(language);
        var snapshotIndex = Array.FindIndex(
            args,
            value => string.Equals(
                value,
                "--snapshot-dir",
                StringComparison.OrdinalIgnoreCase));
        if (snapshotIndex >= 0)
        {
            if (snapshotIndex + 1 >= args.Length)
                throw new ArgumentException("--snapshot-dir requires an output path.");
            RenderSnapshots(args[snapshotIndex + 1]);
            return;
        }

        var light = args.Contains("--light", StringComparer.OrdinalIgnoreCase);
        ThemeManager.Init(light);
        var settings = new Settings
        {
            WidgetVisible = true,
            FirstRunDone = true,
            RefreshIntervalSec = 90,
            BgTransparency = 10,
            DoNotPersist = true,
        };

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var window = new StatusStripWindow(settings);
        window.Title = $"AI Usage Preview — {(light ? "Light" : "Dark")}";
        // Make the preview targetable by Windows UI automation. Production keeps the
        // widget out of the taskbar; this harness changes only its own window instance.
        window.ShowInTaskbar = true;
        window.Closed += (_, _) => app.Shutdown();
        window.Loaded += (_, _) => window.UpdateState(Samples(), SampleTasks());
        DetailsPaneWindow? pane = null;
        window.TogglePaneRequested += () =>
        {
            if (pane is { IsVisible: true })
            {
                pane.Close();
                return;
            }

            pane = new DetailsPaneWindow(settings, projectDocument: SampleProjects());
            pane.UpdateState(Samples(), SampleTasks());
            pane.ShowAbove(window);
        };
        window.Show();
        app.Run();
    }

    static void RenderSnapshots(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        foreach (var language in new[] { UiLanguage.ZhHant, UiLanguage.En })
        {
            L10n.Init(language);
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Init(light);
                var settings = new Settings
                {
                    WidgetVisible = true,
                    FirstRunDone = true,
                    RefreshIntervalSec = 90,
                    BgTransparency = 10,
                    DoNotPersist = true,
                };
                var window = new StatusStripWindow(settings)
                {
                    ShowInTaskbar = false,
                    Topmost = false,
                };
                window.Show();
                window.UpdateState(Samples(), SampleTasks());
                window.UpdateLayout();
                var pane = new DetailsPaneWindow(settings, projectDocument: SampleProjects());
                pane.UpdateState(Samples(), SampleTasks());
                pane.ShowAbove(window);
                pane.UpdateLayout();

                var languageName = language == UiLanguage.En ? "en" : "zh-hant";
                var themeName = light ? "light" : "dark";
                SaveSnapshot(
                    window,
                    Path.Combine(outputDirectory, $"{themeName}-{languageName}.png"));
                SaveSnapshot(
                    pane,
                    Path.Combine(outputDirectory, $"{themeName}-pane-{languageName}.png"));
                var options = (System.Windows.Controls.Button)pane.FindName("ProjectOptions");
                options.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                options.ContextMenu.UpdateLayout();
                SaveSnapshot(options.ContextMenu, Path.Combine(outputDirectory, $"{themeName}-menu-{languageName}.png"));
                options.ContextMenu.IsOpen = false;
                var rows = (System.Windows.Controls.StackPanel)pane.FindName("ProjectOverviewRows");
                var target = (System.Windows.Controls.Border)rows.Children[0];
                var projects = SampleProjects();
                var item = ProjectOverviewBuilder.Build(projects.Projects, projects.Memberships, SampleTasks().Tasks,
                    projects.Updates, projects.Acknowledgements).Projects[0];
                typeof(DetailsPaneWindow).GetMethod("OpenProjectPeek", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(pane, [item, target]);
                var peek = (System.Windows.Controls.Border)pane.FindName("ProjectPeekCard");
                peek.UpdateLayout();
                SaveSnapshot(peek, Path.Combine(outputDirectory, $"{themeName}-peek-{languageName}.png"));
                pane.Close();
                window.Close();
            }
        }

        app.Shutdown();
    }

    static void SaveSnapshot(FrameworkElement window, string outputPath)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    static IReadOnlyDictionary<UsageSource, UsageSnapshot> Samples() =>
        new Dictionary<UsageSource, UsageSnapshot>
    {
        [UsageSource.Claude] = new UsageSnapshot(
            UsageSource.Claude,
            [
                new("session", "Session", 17, DateTimeOffset.Now.AddHours(3)),
                new("weekly_all", "Weekly (all)", 42, DateTimeOffset.Now.AddDays(4)),
            ],
            UsageHealth.Ok,
            DateTimeOffset.Now,
            "Ready"),
        [UsageSource.Codex] = new UsageSnapshot(
            UsageSource.Codex,
            [new("chatgpt_1_10080", L10n.T("chatgpt_limit_weekly"), 35, DateTimeOffset.Now.AddDays(7))],
            UsageHealth.Ok,
            DateTimeOffset.Now,
            "Ready"),
    };

    static StatusBarState SampleTasks()
    {
        var now = DateTimeOffset.Now;
        var tasks = new AgentTask[]
        {
            new()
            {
                Id = "codex:preview",
                Provider = AgentProvider.Codex,
                Title = "Code review",
                Status = AgentTaskStatus.Working,
                Confidence = StateConfidence.Confirmed,
                LastActivity = now,
            },
            new()
            {
                Id = "claude:preview",
                Provider = AgentProvider.Claude,
                Title = "Independent report review",
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = StateConfidence.Confirmed,
                LastActivity = now,
                AttentionReason = "Approval requested",
                StatusDetail = "Waiting for input",
            },
        };
        return new StatusBarState(tasks, 1, 1, new Dictionary<AgentProvider, ProviderHealth>());
    }

    static ProjectOverviewDocument SampleProjects()
    {
        var now = DateTimeOffset.Now;
        return new ProjectOverviewDocument
        {
            Projects =
            [
                new() { Id = "example", Name = "5670 Example Arms", CurrentSummary = "Report revised after independent review" },
                new() { Id = "dashboard", Name = "Household dashboard", LastMeaningfulActivity = now.AddDays(-1) },
            ],
            Memberships =
            [
                new("example", "codex:preview", ProjectMembershipSource.ExplicitReference),
                new("example", "claude:preview", ProjectMembershipSource.ExplicitReference),
                new("dashboard", "codex:dashboard", ProjectMembershipSource.ExplicitReference),
            ],
            Updates =
            [
                new() { Id = "draft", ProjectId = "example", ThreadId = "codex:preview", Revision = 1,
                    OccurredAt = now.AddMinutes(-8), LatestAction = "Updated report following review",
                    Progress = ["Completed calculations", "Revised the conclusion"] },
                new() { Id = "review", ProjectId = "example", ThreadId = "claude:preview", Revision = 1,
                    OccurredAt = now.AddMinutes(-3), LatestAction = "Review finished; needs your answer",
                    Progress = ["Checked report assumptions", "Asked which background level to use"] },
                new() { Id = "earlier", ProjectId = "example", ThreadId = "codex:preview", Revision = 1,
                    OccurredAt = now.AddHours(-1), LatestAction = "Completed survey calculations" },
                new() { Id = "chart", ProjectId = "dashboard", ThreadId = "codex:dashboard", Revision = 1,
                    OccurredAt = now.AddDays(-1), LatestAction = "Updated monthly comparison" },
            ],
            Acknowledgements = [new("chart", 1)],
        };
    }
}
