using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Content-free diagnostic counts for the Codex task collector.</summary>
/// <param name="Health">Current collector health and its stable status code.</param>
/// <param name="SessionsTracked">Number of rollout sessions currently retained in memory.</param>
/// <param name="FilesWatched">Number of tracked rollout files covered by the active directory watcher.</param>
/// <param name="LastEventAge">Age of the latest task evidence, if any.</param>
/// <param name="ParseErrors">Number of malformed JSONL records skipped.</param>
/// <param name="FormatDriftCount">Total number of unfamiliar record shapes.</param>
/// <param name="FormatDriftBySignature">Unfamiliar record counts keyed only by sanitized type names.</param>
/// <param name="WatcherOverflowCount">Number of filesystem watcher overflow events.</param>
public sealed record CodexTaskDiagnostics(
    ProviderHealth Health,
    int SessionsTracked,
    int FilesWatched,
    TimeSpan? LastEventAge,
    long ParseErrors,
    long FormatDriftCount,
    IReadOnlyDictionary<string, long> FormatDriftBySignature,
    int WatcherOverflowCount);
