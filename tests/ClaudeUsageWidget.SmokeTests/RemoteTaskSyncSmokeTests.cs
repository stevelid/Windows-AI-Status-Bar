using System.Security.Cryptography;
using System.Text;
using ClaudeUsageWidget;
using StatusBar.Core.Tasks;

internal static class RemoteTaskSyncSmokeTests
{
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "statusbar-sync-smoke-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected smoke directory.");
        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared);
        static StatusBarState Local(string id, string title) => StatusBarState.Empty with
        {
            Tasks = [new AgentTask { Id = id, Title = title, Provider = AgentProvider.Claude,
                Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed,
                LastActivity = DateTimeOffset.Parse("2030-01-01T12:00:00Z") }],
            WorkingCount = 1,
        };
        try
        {
            await using var first = new RemoteTaskSyncService(shared, "Example office", () => Local("claude:office", "Fictional office work"),
                TimeProvider.System, Path.Combine(root, "first"), startPolling: false);
            await using var second = new RemoteTaskSyncService(shared, "Example home", () => Local("claude:home", "Fictional home work"),
                TimeProvider.System, Path.Combine(root, "second"), startPolling: false);
            for (var pass = 0; pass < 3; pass++)
            {
                await first.PollAsync();
                await second.PollAsync();
            }
            if (first.Current.Count != 1 || !first.Current[0].IsFresh ||
                first.Current[0].Tasks.Single().Title != "Fictional home work" ||
                second.Current.Count != 1 || !second.Current[0].IsFresh ||
                second.Current[0].Tasks.Single().Title != "Fictional office work")
                throw new InvalidOperationException("Two-device exchange failed.");
            foreach (var file in Directory.EnumerateFiles(shared))
            {
                var text = File.ReadAllText(file);
                if (text.Contains("Fictional") || text.Contains("PrivateKey"))
                    throw new InvalidOperationException("Private data leaked into shared files.");
            }
            var identity = File.ReadAllBytes(Path.Combine(root, "first", "task-sync-identity.dat"));
            if (Encoding.UTF8.GetString(identity).Contains("PrivateKey"))
                throw new InvalidOperationException("Identity was not protected.");
            // A restarted receiver accepts the existing file only as stale, then sees the next host heartbeat.
            await using var restarted = new RemoteTaskSyncService(shared, "Example office", () => Local("claude:office", "Fictional office work"),
                TimeProvider.System, Path.Combine(root, "first"), startPolling: false);
            await restarted.PollAsync();
            if (restarted.Current.Single().IsFresh) throw new InvalidOperationException("Cached heartbeat was treated as live.");
            await second.PollAsync();
            await restarted.PollAsync();
            if (!restarted.Current.Single().IsFresh) throw new InvalidOperationException("Restart recovery failed.");
            foreach (var file in Directory.EnumerateFiles(shared, "*.snapshot")) File.WriteAllText(file, "invalid");
            await first.PollAsync();
            if (first.StatusKey != "sync_retrying" || first.Current.Count != 1)
                throw new InvalidOperationException("Malformed exchange did not retain the last snapshot.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
