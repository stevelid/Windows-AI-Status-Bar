using StatusBar.Core.Diagnostics;
using StatusBar.Core.IO;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Codex;

/// <summary>Discovers recent Codex rollouts and keeps their parser state up to date.</summary>
internal sealed class CodexSessionReader : IDisposable
{
    const int InitialTailBytes = 2 * 1024 * 1024;
    const int InitialHeadBytes = 256 * 1024;

    readonly CodexPaths _paths;
    readonly TimeProvider _time;
    readonly TaskTimings _timings;
    readonly FormatDriftCounter _drift = new();
    readonly CodexTitleResolver _titles;
    readonly Dictionary<string, SessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    int _readFailureCount;
    DateTimeOffset _lastDiscovery;
    bool _hasDiscovery;
    bool _disposed;

    internal CodexSessionReader(CodexPaths paths, TimeProvider time, TaskTimings timings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(timings);
        _paths = paths;
        _time = time;
        _timings = timings;
        _titles = new CodexTitleResolver(new IncrementalJsonlReader(paths.SessionIndexPath), _drift);
    }

    /// <summary>
    /// Content-free debug lines (short session ids, turn states, counts). Never includes titles,
    /// prompts, paths or message text.
    /// </summary>
    internal event Action<string>? Trace;

    /// <summary>Raised when a live turn finishes and the AI check is on (see <see cref="FinalMessageCapture"/>).</summary>
    internal event Action<TurnEndInfo>? TurnEnded;

    internal int TrackedFiles => _entries.Count;
    internal int ReadFailureCount => Volatile.Read(ref _readFailureCount);
    internal long ParseErrorCount => _drift.MalformedCount;
    internal long FormatDriftCount => _drift.UnknownCount;
    internal IReadOnlyDictionary<string, long> FormatDriftBySignature => _drift.UnknownBySignature;
    internal IReadOnlyList<(string ThreadId, long Offset, long LastReadStartOffset)> ReaderPositions =>
        _entries.Values.Select(entry => (
            entry.State.ThreadId ?? string.Empty,
            entry.Reader.Offset,
            entry.Reader.LastReadStartOffset)).ToArray();

    internal IReadOnlyList<AgentTask> Reconcile(bool forceDiscovery = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _time.GetUtcNow();
        if (!Directory.Exists(_paths.SessionsDirectory))
        {
            _entries.Clear();
            _hasDiscovery = false;
            _lastDiscovery = default;
            _titles.Refresh();
            return [];
        }

        if (forceDiscovery || !_hasDiscovery || now - _lastDiscovery >= _timings.ReconcileInterval)
        {
            var discovered = DiscoverFiles(now);
            var recovered = new List<CodexSessionState>();
            foreach (var path in discovered)
            {
                if (_entries.ContainsKey(path)) continue;
                try
                {
                    var created = CreateEntry(path, now);
                    _entries[path] = created;
                    recovered.Add(created.State);
                    Emit($"session {ShortId(created.State)} tracked: {Describe(created.State)}, meta={(created.State.ThreadId is null ? "no" : "yes")}");
                }
                catch (IOException ex)
                {
                    Interlocked.Increment(ref _readFailureCount);
                    Emit($"session read failed on discovery ({ex.GetType().Name})");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Interlocked.Increment(ref _readFailureCount);
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

        _titles.Refresh();
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
                    CodexRolloutParser.Apply(entry.State, line, fallbackTime, _drift);
                var after = Describe(entry.State);
                RaiseTurnEnded(entry.State, finalBefore, now);
                if (after != before)
                    Emit($"session {ShortId(entry.State)} {before} -> {after} ({result.Lines.Count} new records)");
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _readFailureCount);
                Emit($"session {ShortId(entry.State)} read failed ({ex.GetType().Name})");
            }
            catch (UnauthorizedAccessException ex)
            {
                Interlocked.Increment(ref _readFailureCount);
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
        var state = new CodexSessionState { FileKey = FileKeyFromPath(path) };
        var fallbackTime = GetFileTime(path, now);
        var tail = TailReader.ReadWithOffset(path, InitialTailBytes);
        foreach (var line in ReadHeadLines(path, tail.FirstLineOffset))
            CodexRolloutParser.Apply(state, line, fallbackTime, _drift);
        foreach (var line in tail.Lines)
            CodexRolloutParser.Apply(state, line, fallbackTime, _drift);

        var reader = new IncrementalJsonlReader(path);
        reader.StartAt(tail.Offset);
        return new SessionEntry(state, reader);
    }

    /// <summary>Attaches the AI's answer (or null: rules decide) to the session whose finished turn it belongs to.</summary>
    internal bool ApplyJudgment(string taskKey, string evidenceKey, TurnEndJudgment? judgment)
    {
        foreach (var entry in _entries.Values)
        {
            var state = entry.State;
            if (state.FinalMessageAt is not DateTimeOffset at ||
                !string.Equals("codex:" + ShortId(state), taskKey, StringComparison.Ordinal) ||
                !string.Equals(TurnVerdictPolicy.EvidenceKeyFor(at), evidenceKey, StringComparison.Ordinal)) continue;
            state.Verdict = new TurnVerdict(evidenceKey, judgment);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Turns that had already finished when their session was first read never raise TurnEnded themselves, so after a
    /// restart they would fall back to the rules and re-raise alerts the AI had judged calm (D26). The recent ones,
    /// capped, are judged now.
    /// </summary>
    void RaiseRecoveredTurns(List<CodexSessionState> recovered, DateTimeOffset now)
    {
        var candidates = recovered.Where(state => state.FinalMessageAt is not null && state.FinalMessageTail is not null).ToList();
        var chosen = TurnVerdictPolicy.SelectStartupJudgments(candidates.Select(state => state.FinalMessageAt!.Value).ToArray(), now);
        foreach (var index in chosen) RaiseTurnEnded(candidates[index], null, now);
    }

    void RaiseTurnEnded(CodexSessionState state, DateTimeOffset? finalBefore, DateTimeOffset now)
    {
        if (state.FinalMessageAt is not DateTimeOffset at || at == finalBefore || state.FinalMessageTail is not { } text) return;
        // The quiet wait for the answer counts from now, not from a final message that may be minutes old (D26).
        state.JudgmentRequestedAt = now;
        try
        {
            TurnEnded?.Invoke(new TurnEndInfo(
                AgentProvider.Codex,
                "codex:" + ShortId(state),
                TurnVerdictPolicy.EvidenceKeyFor(at),
                state.EndedWithStructuredQuestion ? "card" : state.EndedWithQuestion ? "question" : "none",
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
        catch { /* logging must never break collection */ }
    }

    // First 8 characters of the thread id (or file uuid): enough to correlate lines, not content.
    // Last eight characters: thread ids are UUIDv7, so the leading characters are a timestamp
    // shared by sessions started close together, while the tail is random. Matches TaskChangeLog.
    internal static string ShortId(CodexSessionState state)
    {
        var key = state.ThreadId ?? state.FileKey ?? "unknown";
        return key.Length > 8 ? key[^8..] : key;
    }

    // Turn state, question flag and pending-call count only.
    static string Describe(CodexSessionState state) =>
        $"turn={state.Turn}, question={(state.EndedWithStructuredQuestion ? "card" : state.EndedWithQuestion ? "yes" : "no")}, pending={state.PendingCalls.Count}";

    // Rollout files are named rollout-<timestamp>-<uuid>.jsonl; the uuid matches the thread id.
    internal static string FileKeyFromPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length >= 36 && Guid.TryParse(stem.AsSpan(stem.Length - 36), out _)
            ? stem[^36..]
            : stem;
    }

    IReadOnlyList<AgentTask> MapAndMerge(DateTimeOffset now)
    {
        var mapped = _entries.Values
            // ⚠️ A-X6 Codex's approval-review ("guardian") sessions are internal safety checks, not tasks.
            .Where(entry => !entry.State.IsGuardianReview)
            .Select(entry => (entry.State, Task: CodexTaskMapper.Map(entry.State, now, _timings)))
            .Select(pair => (pair.State, Task: pair.Task with { Title = _titles.Resolve(pair.State) }))
            .ToList();
        var byThreadId = mapped
            .Select((item, index) => (item, index))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.item.State.ThreadId))
            .Select(pair => (ThreadId: pair.item.State.ThreadId!, pair.index))
            .GroupBy(pair => pair.ThreadId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var removedChildren = new HashSet<string>(StringComparer.Ordinal);

        for (var childIndex = 0; childIndex < mapped.Count; childIndex++)
        {
            var child = mapped[childIndex];
            if (!IsSubAgent(child.State.Source) || string.IsNullOrWhiteSpace(child.State.ParentThreadId) ||
                !byThreadId.TryGetValue(child.State.ParentThreadId, out var parentIndex)) continue;

            var parent = mapped[parentIndex];
            mapped[parentIndex] = (parent.State, MergeChild(parent.Task, child.Task));
            removedChildren.Add(child.Task.Id);
        }

        return mapped
            .Where(item => !removedChildren.Contains(item.Task.Id))
            .Select(item => item.Task)
            .OrderByDescending(task => task.Status == AgentTaskStatus.NeedsAttention)
            .ThenByDescending(task => task.LastActivity)
            .ToArray();
    }

    static AgentTask MergeChild(AgentTask parent, AgentTask child)
    {
        var merged = parent with { LastActivity = Max(parent.LastActivity, child.LastActivity) };
        if (child.Status == AgentTaskStatus.NeedsAttention)
        {
            return merged with
            {
                Status = AgentTaskStatus.NeedsAttention,
                Confidence = child.Confidence,
                AttentionReason = child.AttentionReason,
                StatusDetail = child.StatusDetail,
                EvidenceKey = child.EvidenceKey,
            };
        }

        if (child.Status == AgentTaskStatus.Working && parent.Status != AgentTaskStatus.NeedsAttention)
        {
            return merged with
            {
                Status = AgentTaskStatus.Working,
                Confidence = child.Confidence,
                AttentionReason = null,
                EvidenceKey = null,
            };
        }

        return merged;
    }

    IReadOnlyList<string> DiscoverFiles(DateTimeOffset now)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localDate = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        for (var daysBack = 0; daysBack < 3; daysBack++)
        {
            var date = localDate.AddDays(-daysBack);
            var dateDirectory = Path.Combine(
                _paths.SessionsDirectory,
                date.Year.ToString("D4"),
                date.Month.ToString("D2"),
                date.Day.ToString("D2"));
            AddFiles(dateDirectory, SearchOption.TopDirectoryOnly, files, _ => true);
        }

        var recentAfter = now - _timings.CodexRecentFileWindow;
        AddFiles(_paths.SessionsDirectory, SearchOption.AllDirectories, files, path =>
        {
            try
            {
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                return modified >= recentAfter;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        });
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static void AddFiles(
        string directory,
        SearchOption searchOption,
        HashSet<string> files,
        Func<string, bool> include)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", searchOption))
            {
                if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                if (include(path)) files.Add(path);
            }
        }
        catch (IOException)
        {
            // An inaccessible or concurrently removed subdirectory is picked up on the next rescan.
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible or concurrently removed subdirectory is picked up on the next rescan.
        }
    }

    static IReadOnlyList<string> ReadHeadLines(string path, long endBeforeOffset)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            var byteCount = (int)Math.Min(Math.Min(stream.Length, InitialHeadBytes), endBeforeOffset);
            var bytes = new byte[byteCount];
            var read = 0;
            while (read < bytes.Length)
            {
                var current = stream.Read(bytes, read, bytes.Length - read);
                if (current == 0) break;
                read += current;
            }
            if (read != bytes.Length) Array.Resize(ref bytes, read);
            return JsonlLineCodec.SplitCompleteLines(bytes).Lines;
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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

    static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    static bool IsSubAgent(string? source) =>
        string.Equals(source, "sub-agent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "sub_agent", StringComparison.OrdinalIgnoreCase);

    sealed record SessionEntry(CodexSessionState State, IncrementalJsonlReader Reader);
}
