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

    /// <summary>
    /// Content-free debug lines (short session id, turn state, question flag, pending tool counts by
    /// kind). Never includes text, tool inputs, tool names or paths. Subscribers must not throw.
    /// </summary>
    internal event Action<string>? Trace;

    /// <summary>Raised when a live turn finishes and the AI check is on (see <see cref="StatusBar.Core.Judgment.FinalMessageCapture"/>).</summary>
    internal event Action<StatusBar.Core.Judgment.TurnEndInfo>? TurnEnded;

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
            var recovered = new List<ClaudeCodeSessionState>();
            foreach (var path in discovered)
            {
                if (_entries.ContainsKey(path)) continue;
                try
                {
                    var created = CreateEntry(path, now);
                    _entries[path] = created;
                    recovered.Add(created.State);
                    Emit($"session {ShortId(created.State)} tracked: {Describe(created.State)}");
                }
                catch (IOException ex)
                {
                    // A file being written or replaced is retried on the next watcher event or scan.
                    Emit($"session read failed on discovery ({ex.GetType().Name})");
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Do not expose per-user paths in errors; inaccessible files are skipped.
                    Emit($"session read failed on discovery ({ex.GetType().Name})");
                }
            }

            RaiseRecoveredTurns(recovered, now);

            var discoveredSet = new HashSet<string>(discovered, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _entries.Keys.Where(path => !discoveredSet.Contains(path)).ToArray())
            {
                Emit($"session {ShortId(_entries[stale].State)} no longer recent; dropped");
                _entries.Remove(stale);
            }

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
                    var recreated = CreateEntry(path, now);
                    _entries[path] = recreated;
                    Emit($"session {ShortId(recreated.State)} file reset; re-read: {Describe(recreated.State)}");
                    continue;
                }

                if (result.Lines.Count == 0) continue;
                var before = Describe(entry.State);
                var finalBefore = entry.State.FinalMessageAt;
                foreach (var line in result.Lines)
                    ClaudeCodeTranscriptParser.Apply(entry.State, line, fallbackTime, _drift);
                var after = Describe(entry.State);
                RaiseTurnEnded(entry.State, finalBefore, now);
                if (after != before)
                    Emit($"session {ShortId(entry.State)} {before} -> {after} ({result.Lines.Count} new records)");
            }
            catch (IOException ex)
            {
                // Retain current in-memory state and retry on the next pass.
                Emit($"session {ShortId(entry.State)} read failed ({ex.GetType().Name})");
            }
            catch (UnauthorizedAccessException ex)
            {
                // Retain current in-memory state and retry on the next pass.
                Emit($"session {ShortId(entry.State)} read failed ({ex.GetType().Name})");
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
        // Subagent transcripts share their parent's sessionId; fold them into the parent's activity.
        var subagents = _entries.Values
            .Where(entry => entry.State.IsSidechainOnly && entry.State.SessionId is not null)
            .GroupBy(entry => entry.State.SessionId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ClaudeSubagentActivity(
                    group.Max(entry => entry.State.LastActivity),
                    group.Any(entry => !entry.State.SidechainEnded &&
                        now - entry.State.LastActivity < _timings.ClaudeCodeWorkingStaleAfter)),
                StringComparer.Ordinal);
        return _entries.Values
            .Where(entry => !entry.State.IsSidechainOnly)
            .Select(entry => ClaudeCodeTaskMapper.Map(
                entry.State,
                now,
                _timings,
                entry.State.SessionId is { } id && subagents.TryGetValue(id, out var activity) ? activity : null))
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

    /// <summary>Attaches the AI's answer (or null: rules decide) to the session whose finished turn it belongs to.</summary>
    internal bool ApplyJudgment(string taskKey, string evidenceKey, StatusBar.Core.Judgment.TurnEndJudgment? judgment)
    {
        foreach (var entry in _entries.Values)
        {
            var state = entry.State;
            if (state.IsSidechainOnly || state.FinalMessageAt is not DateTimeOffset at ||
                !string.Equals("claude:" + ShortId(state), taskKey, StringComparison.Ordinal) ||
                !string.Equals(StatusBar.Core.Judgment.TurnVerdictPolicy.EvidenceKeyFor(at), evidenceKey, StringComparison.Ordinal)) continue;
            state.Verdict = new StatusBar.Core.Judgment.TurnVerdict(evidenceKey, judgment);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Turns that had already finished when their session was first read never raise TurnEnded themselves, so after a
    /// restart they would fall back to the rules and re-raise alerts the AI had judged calm (D26). The recent ones,
    /// capped, are judged now.
    /// </summary>
    void RaiseRecoveredTurns(List<ClaudeCodeSessionState> recovered, DateTimeOffset now)
    {
        var candidates = recovered.Where(state => state.FinalMessageAt is not null && state.FinalMessageTail is not null).ToList();
        var chosen = StatusBar.Core.Judgment.TurnVerdictPolicy.SelectStartupJudgments(candidates.Select(state => state.FinalMessageAt!.Value).ToArray(), now);
        foreach (var index in chosen) RaiseTurnEnded(candidates[index], null, now);
    }

    void RaiseTurnEnded(ClaudeCodeSessionState state, DateTimeOffset? finalBefore, DateTimeOffset now)
    {
        if (state.IsSidechainOnly || state.FinalMessageAt is not DateTimeOffset at || at == finalBefore || state.FinalMessageTail is not { } text) return;
        // The quiet wait for the answer counts from now, not from a final message that may be minutes old (D26).
        state.JudgmentRequestedAt = now;
        try
        {
            TurnEnded?.Invoke(new StatusBar.Core.Judgment.TurnEndInfo(
                AgentProvider.Claude,
                "claude:" + ShortId(state),
                StatusBar.Core.Judgment.TurnVerdictPolicy.EvidenceKeyFor(at),
                state.EndedWithQuestion ? "question" : "none",
                text,
                state.FinalMessageStart));
        }
        catch (Exception)
        {
            // The optional check must never interrupt task collection.
        }
    }

    void Emit(string message)
    {
        try { Trace?.Invoke(message); }
        catch (Exception)
        {
            // Logging must never interrupt task collection.
        }
    }

    // Last eight characters of the session id (matches TaskChangeLog), plus "/sub" for a sub-agent
    // transcript, which shares its parent's session id.
    internal static string ShortId(ClaudeCodeSessionState state)
    {
        var key = string.IsNullOrWhiteSpace(state.SessionId) ? "unknown" : state.SessionId;
        var shortKey = key.Length > 8 ? key[^8..] : key;
        return state.IsSidechainOnly ? shortKey + "/sub" : shortKey;
    }

    // Turn state, question flag and pending tool counts by kind only.
    internal static string Describe(ClaudeCodeSessionState state)
    {
        var pending = state.PendingTools.Values;
        var questions = pending.Count(tool => tool.Kind == ClaudePendingToolKind.Question);
        var plans = pending.Count(tool => tool.Kind == ClaudePendingToolKind.PlanApproval);
        return $"turn={state.Turn}, question={(state.EndedWithQuestion ? "yes" : "no")}, " +
               $"pending={pending.Count} (ask={questions}, plan={plans})";
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
