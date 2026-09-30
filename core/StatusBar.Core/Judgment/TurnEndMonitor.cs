using System.Globalization;

namespace StatusBar.Core.Judgment;

/// <summary>Running totals for the side-by-side comparison; content-free.</summary>
public sealed record TurnEndStats(
    int Judged,
    int Agreed,
    int Disagreed,
    int StructuredQuestions,
    int Failed,
    int Skipped,
    double AverageLatencyMs,
    long InputTokens,
    long OutputTokens);

/// <summary>
/// Runs the AI check on each finished turn and records how it compares with the built-in rules, so the
/// two can be judged after a period of use. The answer goes back through <c>resolved</c>; a failed,
/// skipped or slow call resolves with null and the rules decide. Each decision is logged with
/// probabilities and the task's short id but no message text.
/// </summary>
public sealed class TurnEndMonitor : IDisposable
{
    /// <summary>Probability at or above which a statement counts as true when comparing with the rules.</summary>
    public const double Threshold = TurnVerdictPolicy.AskThreshold;

    const int MaximumConcurrentCalls = 2;
    const int RememberedTurns = 200;
    const int FailuresBeforePause = 5;
    static readonly TimeSpan PauseFor = TimeSpan.FromMinutes(5);

    readonly Func<ITurnEndClassifier?> _classifier;
    readonly Action<string> _log;
    readonly TimeProvider _time;
    readonly Action<TurnEndInfo, TurnEndJudgment?>? _resolved;
    readonly Func<bool>? _affectsState;
    readonly SemaphoreSlim _slots = new(MaximumConcurrentCalls);
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Queue<string> _seenOrder = new();

    int _judged, _agreed, _disagreed, _cards, _failed, _skipped;
    double _latencyTotalMs;
    long _inputTokens, _outputTokens;
    int _consecutiveFailures;
    DateTimeOffset _pausedUntil;

    /// <summary>Creates a monitor; <paramref name="classifier"/> returns null while the feature is off.</summary>
    /// <param name="resolved">Called with the answer for a turn, or null when the rules should decide.</param>
    /// <param name="affectsState">Whether answers currently change task state; only used to label the log.</param>
    public TurnEndMonitor(
        Func<ITurnEndClassifier?> classifier,
        Action<string> log,
        TimeProvider time,
        Action<TurnEndInfo, TurnEndJudgment?>? resolved = null,
        Func<bool>? affectsState = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _classifier = classifier;
        _log = log;
        _time = time;
        _resolved = resolved;
        _affectsState = affectsState;
    }

    /// <summary>The totals so far.</summary>
    public TurnEndStats Stats
    {
        get
        {
            lock (_gate)
                return new TurnEndStats(
                    _judged, _agreed, _disagreed, _cards, _failed, _skipped,
                    _judged == 0 ? 0 : _latencyTotalMs / _judged, _inputTokens, _outputTokens);
        }
    }

    /// <summary>Queues a finished turn for checking. Returns at once; the call runs in the background.</summary>
    public void Observe(TurnEndInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var classifier = _classifier();
        if (classifier is null) return;
        var paused = false;
        lock (_gate)
        {
            if (!_seen.Add(info.TaskKey + "|" + info.EvidenceKey)) return;
            _seenOrder.Enqueue(info.TaskKey + "|" + info.EvidenceKey);
            while (_seenOrder.Count > RememberedTurns) _seen.Remove(_seenOrder.Dequeue());

            if (_time.GetUtcNow() < _pausedUntil)
            {
                _skipped++;
                paused = true;
            }
        }

        if (paused)
        {
            Resolve(info, null);
            return;
        }

        _ = Task.Run(() => JudgeAsync(classifier, info));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    async Task JudgeAsync(ITurnEndClassifier classifier, TurnEndInfo info)
    {
        try
        {
            await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var started = _time.GetTimestamp();
            var judgment = await classifier.ClassifyAsync(info.Text, _stop.Token).ConfigureAwait(false);
            var elapsedMs = _time.GetElapsedTime(started).TotalMilliseconds;
            Record(info, judgment, elapsedMs);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordFailure(info, ex is TurnEndClassifierException classifierEx ? classifierEx.Code : ex.GetType().Name);
        }
        finally
        {
            _slots.Release();
        }
    }

    void Record(TurnEndInfo info, TurnEndJudgment judgment, double elapsedMs)
    {
        var jevAsks = judgment.AsksUser >= Threshold;
        string verdict;
        lock (_gate)
        {
            _judged++;
            _latencyTotalMs += elapsedMs;
            _inputTokens += judgment.InputTokens;
            _outputTokens += judgment.OutputTokens;
            _consecutiveFailures = 0;

            // The AI never sees a structured question card, so those cases are counted apart.
            if (info.Heuristic == "card")
            {
                _cards++;
                verdict = "n/a (card)";
            }
            else if ((info.Heuristic == "question") == jevAsks)
            {
                _agreed++;
                verdict = "yes";
            }
            else
            {
                _disagreed++;
                verdict = "NO";
            }
        }

        // A structured question card is always decided by the rules: the AI cannot see it.
        var applied = info.Heuristic != "card" && (_affectsState?.Invoke() ?? false) ? "jev" : "rules";
        _log(string.Format(
            CultureInfo.InvariantCulture,
            "Jev {0}: rules={1} | asks={2:0.00} review={3:0.00} followup={4:0.00} finished={5:0.00} | agree={6} | applied={7} | {8:0} ms, {9}+{10} tokens",
            info.TaskKey, info.Heuristic, judgment.AsksUser, judgment.NeedsReview, judgment.OffersFollowUp,
            judgment.Finished, verdict, applied, elapsedMs, judgment.InputTokens, judgment.OutputTokens));
        Resolve(info, judgment);
    }

    // The provider must always hear back, so a turn never waits on an answer that will not come.
    void Resolve(TurnEndInfo info, TurnEndJudgment? judgment)
    {
        try
        {
            _resolved?.Invoke(info, judgment);
        }
        catch (Exception)
        {
            // A provider that has gone away must not break the check.
        }
    }

    void RecordFailure(TurnEndInfo info, string code)
    {
        var paused = false;
        lock (_gate)
        {
            _failed++;
            if (++_consecutiveFailures >= FailuresBeforePause)
            {
                _consecutiveFailures = 0;
                _pausedUntil = _time.GetUtcNow() + PauseFor;
                paused = true;
            }
        }

        _log($"Jev {info.TaskKey}: failed ({code}); the built-in rules decide");
        if (paused) _log($"Jev paused for {PauseFor.TotalMinutes:0} minutes after repeated failures");
        Resolve(info, null);
    }
}
