namespace StatusBar.Core.Projects;

/// <summary>Bounds initial project discovery independently of the short-lived attention classifier.</summary>
public static class ProjectSummaryBootstrap
{
    public const int MaximumThreadsPerProvider = 10;
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static IReadOnlyList<int> Select(IReadOnlyList<DateTimeOffset> times, DateTimeOffset now) =>
        Enumerable.Range(0, times.Count).Where(i => times[i] <= now && now - times[i] <= Window)
            .OrderByDescending(i => times[i]).Take(MaximumThreadsPerProvider).ToArray();
}
