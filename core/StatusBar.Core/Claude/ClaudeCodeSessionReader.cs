using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Claude;

/// <summary>Discovers recent Claude Code transcripts and keeps each session parser state current.</summary>
internal sealed class ClaudeCodeSessionReader : IDisposable
{
    const int InitialTailBytes = 2 * 1024 * 1024;

    readonly ClaudeCodePaths _paths;
    readonly TimeProvider _time;
    readonly TaskTimings _timings;
    readonly FormatDriftCounter _drift = new();
    readonly Dictionary<string, SessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _lastDiscovery;
    bool _hasDiscovery;
    bool _disposed;

    internal ClaudeCodeSessionReader(ClaudeCodePaths paths, TimeProvider time, TaskTimings timings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(timings);
        _paths = paths;
        _time = time;
        _timings = timings;
    }

    internal int TrackedFiles => _entries.Count;
    internal IReadOnlyList<(string SessionId, long Offset, long LastReadStartOffset)> ReaderPositions =>
        _entries.Values.Select(entry => (
            entry.State.SessionId ?? string.Empty,
            entry.Reader.Offset,
            entry.Reader.LastReadStartOffset)).ToArray();

    internal IReadOnlyList<AgentTask> Reconcile(bool forceDiscovery = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _time.GetUtcNow();
        if (!Directory.Exists(_paths.ProjectsDirectory))
        {
            _entries.Clear();
            _hasDiscovery = false;
            _lastDiscovery = default;
            return [];
        }

        if (forceDiscovery || !_hasDiscovery || now - _lastDiscovery >= _timings.ReconcileInterval)
        {
            var discovered = DiscoverFiles(now);
            foreach (var path in discovered)
            {
                if (_entries.ContainsKey(path)) continue;
                try
                {
                    _entries[path] = CreateEntry(path, now);
                }
                catch (IOException)
                {
                    // A file being written or replaced is retried on the next watcher event or scan.
                }
                catch (UnauthorizedAccessException)
                {
                    // Do not expose per-user paths in errors; inaccessible files are skipped.
                }
            }

            var discoveredSet = new HashSet<string>(discovered, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _entries.Keys.Where(path => !discoveredSet.Contains(path)).ToArray())
                _entries.Remove(stale);

            _lastDiscovery = now;
            _hasDiscovery = true;
        }

        foreach (var (path, entry) in _entries.ToArray())
        {
            try
            {
                var fallbackTime = GetFileTime(path, now);
                var result = entry.Reader.ReadNewLines();
                if (result.Reset)
                {
                    _entries[path] = CreateEntry(path, now);
                    continue;
                }

                foreach (var line in result.Lines)
                    ClaudeCodeTranscriptParser.Apply(entry.State, line, fallbackTime, _drift);
            }
            catch (IOException)
            {
                // Retain current in-memory state and retry on the next pass.
            }
            catch (UnauthorizedAccessException)
            {
                // Retain current in-memory state and retry on the next pass.
            }
        }

        return MapAndMerge(now);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entries.Clear();
    }

    SessionEntry CreateEntry(string path, DateTimeOffset now)
    {
        var state = new ClaudeCodeSessionState();
        var fallbackTime = GetFileTime(path, now);
        var tail = TailReader.ReadWithOffset(path, InitialTailBytes);
        foreach (var line in tail.Lines)
            ClaudeCodeTranscriptParser.Apply(state, line, fallbackTime, _drift);

        if (string.IsNullOrWhiteSpace(state.SessionId))
            state.SessionId = Path.GetFileNameWithoutExtension(path);

        var reader = new IncrementalJsonlReader(path);
        reader.StartAt(tail.Offset);
        return new SessionEntry(state, reader);
    }

    IReadOnlyList<AgentTask> MapAndMerge(DateTimeOffset now)
    {
        return _entries.Values
            .Where(entry => !entry.State.IsSidechainOnly)
            .Select(entry => ClaudeCodeTaskMapper.Map(entry.State, now, _timings))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(task => task.LastActivity).First())
            .OrderByDescending(task => task.Status == AgentTaskStatus.NeedsAttention)
            .ThenByDescending(task => task.LastActivity)
            .ToArray();
    }

    IReadOnlyList<string> DiscoverFiles(DateTimeOffset now)
    {
        var files = new List<string>();
        var minimumWriteTime = now - _timings.ClaudeCodeRecentFileWindow;
        try
        {
            // ⚠️ A-K1 The projects JSONL tree is confirmed for the desktop Code tab; other Claude Code roots remain unverified.
            foreach (var path in Directory.EnumerateFiles(_paths.ProjectsDirectory, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (GetFileTime(path, DateTimeOffset.MinValue) >= minimumWriteTime)
                        files.Add(path);
                }
                catch (IOException)
                {
                    // The file may have been removed between enumeration and the timestamp check.
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible files without surfacing their path.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            _hasDiscovery = false;
        }
        catch (IOException)
        {
            // An inaccessible or concurrently removed project folder is retried on the next scan.
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible or concurrently removed project folder is retried on the next scan.
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static DateTimeOffset GetFileTime(string path, DateTimeOffset fallback)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (IOException)
        {
            return fallback;
        }
        catch (UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    sealed record SessionEntry(ClaudeCodeSessionState State, IncrementalJsonlReader Reader);
}
