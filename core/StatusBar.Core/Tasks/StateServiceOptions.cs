namespace StatusBar.Core.Tasks;

/// <summary>Controls how long completed, failed, and unknown tasks remain visible.</summary>
public sealed record StateServiceOptions(
    TimeSpan RecentlyCompletedFor,
    TimeSpan UnknownVisibleFor)
{
    /// <summary>Standard retention windows used by the status bar.</summary>
    public static StateServiceOptions Default { get; } = new(
        TaskTimings.Default.RecentlyCompletedFor,
        TaskTimings.Default.UnknownVisibleFor);
}
