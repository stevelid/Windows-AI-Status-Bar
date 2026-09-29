using System.IO.Compression;
using StatusBar.Core.Codex;
using StatusBar.Core.Diagnostics;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Diagnostics;

public sealed class DiagnosticReportBuilderTests
{
    [Fact]
    public void Report_counts_tasks_without_including_task_titles_or_references()
    {
        const string title = "PRIVATE synthetic task title";
        const string taskId = "codex:synthetic-session-id";
        const string sessionReference = "C:/synthetic/private/session.jsonl";
        var state = new StatusBarState(
            new[]
            {
                new AgentTask
                {
                    Id = taskId,
                    Provider = AgentProvider.Codex,
                    Title = title,
                    Status = AgentTaskStatus.NeedsAttention,
                    Confidence = StateConfidence.Confirmed,
                    LastActivity = DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
                    EvidenceKey = "synthetic-evidence",
                    SessionReference = sessionReference,
                },
            },
            WorkingCount: 0,
            AttentionCount: 1,
            new Dictionary<AgentProvider, ProviderHealth>
            {
                [AgentProvider.Codex] = new(ProviderHealthState.Ok, "Ok", DateTimeOffset.Parse("2030-01-01T00:00:00Z")),
            });

        var report = DiagnosticReportBuilder.Build(
            "3.0.0",
            "3.0.0+abcdef0",
            new[] { new DiagnosticUsageProvider("Codex", "Ok", null, "Ready") },
            state,
            new CodexTaskDiagnostics(
                new ProviderHealth(ProviderHealthState.Ok, "Ok", null),
                SessionsTracked: 1,
                FilesWatched: 1,
                LastEventAge: TimeSpan.FromSeconds(2),
                ParseErrors: 0,
                FormatDriftCount: 0,
                FormatDriftBySignature: new Dictionary<string, long>(),
                WatcherOverflowCount: 0),
            "Automatic",
            "codex.exe",
            "1.0.0");

        Assert.Contains("Tasks.Total: 1", report, StringComparison.Ordinal);
        Assert.Contains("Tasks.NeedsAttention: 1", report, StringComparison.Ordinal);
        Assert.DoesNotContain(title, report, StringComparison.Ordinal);
        Assert.DoesNotContain(taskId, report, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionReference, report, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_writer_creates_the_three_fixed_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"status-bar-diagnostics-{Guid.NewGuid():N}.zip");
        try
        {
            DiagnosticBundleWriter.Write(path, "report", "log", "drift");

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(
                new[] { "format-drift.txt", "log-tail.txt", "report.txt" },
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name));
            Assert.Equal("report", ReadEntry(archive, "report.txt"));
            Assert.Equal("log", ReadEntry(archive, "log-tail.txt"));
            Assert.Equal("drift", ReadEntry(archive, "format-drift.txt"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
