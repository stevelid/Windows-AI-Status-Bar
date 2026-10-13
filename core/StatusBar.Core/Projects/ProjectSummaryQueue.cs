namespace StatusBar.Core.Projects;

/// <summary>Coalesces completed turns per conversation, applies a launch budget and rejects superseded work.</summary>
public sealed class ProjectSummaryQueue(TimeProvider timeProvider)
{
    const int MaximumPendingThreads = 20;
    const int MaximumLaunchesPerHour = 12;
    const int MaximumCoalescedTurns = 8;
    readonly object _gate = new();
    readonly Dictionary<string, (ProjectSummaryRequest Request, long Generation)> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, ProjectSummaryWorkItem> _inFlight = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();
    readonly Dictionary<string, long> _latestGeneration = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> _scheduledEvidence = new(StringComparer.Ordinal);
    readonly Queue<DateTimeOffset> _launches = new();
    DateTimeOffset _backoffUntil;
    int _failureCount;

    public bool Enqueue(ProjectSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProjectSummaryValidator.IsValidRequest(request, enforceInputBudget: false)) return false;
        request = FitBudget(request);
        if (!ProjectSummaryValidator.IsValidRequest(request)) return false;
        lock (_gate)
        {
            if (_scheduledEvidence.TryGetValue(request.ThreadId, out var scheduled) && scheduled.Contains(request.EvidenceId)) return false;
            if (!_pending.TryGetValue(request.ThreadId, out var queued))
            {
                if (_pending.Count >= MaximumPendingThreads && !_inFlight.ContainsKey(request.ThreadId)) return false;
                _order.Enqueue(request.ThreadId);
                queued = _inFlight.TryGetValue(request.ThreadId, out var active)
                    ? (Coalesce(active.Request, request), active.Generation)
                    : (request, 0);
            }
            else queued = (Coalesce(queued.Request, request), queued.Generation);
            queued = (FitBudget(queued.Item1), queued.Generation);
            var generation = _latestGeneration.GetValueOrDefault(request.ThreadId) + 1;
            _latestGeneration[request.ThreadId] = generation;
            _pending[request.ThreadId] = (queued.Item1, generation);
            if (!_scheduledEvidence.TryGetValue(request.ThreadId, out scheduled)) _scheduledEvidence[request.ThreadId] = scheduled = new(StringComparer.Ordinal);
            scheduled.Add(request.EvidenceId);
            return true;
        }
    }

    public bool TryDequeue(out ProjectSummaryWorkItem item)
    {
        lock (_gate)
        {
            while (_order.TryDequeue(out var threadId))
            {
                if (!_pending.Remove(threadId, out var queued)) continue;
                item = new ProjectSummaryWorkItem(queued.Request, queued.Generation);
                _inFlight[threadId] = item;
                return true;
            }
        }
        item = default!;
        return false;
    }

    public bool IsCurrent(ProjectSummaryWorkItem item)
    {
        lock (_gate) return _latestGeneration.TryGetValue(item.Request.ThreadId, out var generation) && generation == item.Generation;
    }

    /// <summary>Releases event-level de-duplication after a worker attempt; durable coverage handles successful retries.</summary>
    public void Complete(ProjectSummaryWorkItem item)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(item.Request.ThreadId, out var active) && active.Generation == item.Generation)
                _inFlight.Remove(item.Request.ThreadId);
            if (_latestGeneration.TryGetValue(item.Request.ThreadId, out var generation) && generation != item.Generation) return;
            if (_pending.ContainsKey(item.Request.ThreadId)) return;
            _scheduledEvidence.Remove(item.Request.ThreadId);
        }
    }

    public void Requeue(ProjectSummaryWorkItem item)
    {
        lock (_gate)
        {
            if (!_latestGeneration.TryGetValue(item.Request.ThreadId, out var generation) || generation != item.Generation ||
                _pending.ContainsKey(item.Request.ThreadId)) return;
            _pending[item.Request.ThreadId] = (item.Request, item.Generation);
            _order.Enqueue(item.Request.ThreadId);
        }
    }

    /// <summary>Invalidates queued and active results after an explicit clear or project deletion.</summary>
    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var threadId in _latestGeneration.Keys.ToArray()) _latestGeneration[threadId]++;
            _pending.Clear();
            _order.Clear();
            _scheduledEvidence.Clear();
        }
    }

    public TimeSpan GetLaunchDelay()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            while (_launches.Count > 0 && now - _launches.Peek() >= TimeSpan.FromHours(1)) _launches.Dequeue();
            var rateDelay = _launches.Count < MaximumLaunchesPerHour ? TimeSpan.Zero : TimeSpan.FromHours(1) - (now - _launches.Peek());
            return rateDelay > _backoffUntil - now ? rateDelay : (_backoffUntil > now ? _backoffUntil - now : TimeSpan.Zero);
        }
    }

    public void RecordLaunch()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            while (_launches.Count > 0 && now - _launches.Peek() >= TimeSpan.FromHours(1)) _launches.Dequeue();
            _launches.Enqueue(now);
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failureCount = Math.Min(_failureCount + 1, 6);
            var seconds = Math.Min(300, 5 * (1 << (_failureCount - 1)));
            _backoffUntil = timeProvider.GetUtcNow().AddSeconds(seconds);
        }
    }

    public void RecordSuccess()
    {
        lock (_gate) { _failureCount = 0; _backoffUntil = default; }
    }

    static ProjectSummaryRequest Coalesce(ProjectSummaryRequest earlier, ProjectSummaryRequest latest)
    {
        var evidenceAll = (earlier.EarlierEvidence ?? []).Append(new ProjectSummaryEvidence(
            earlier.EvidenceId, earlier.UserRequest, earlier.AssistantFinal, earlier.OccurredAt))
            .Concat(latest.EarlierEvidence ?? [])
            .DistinctBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        var evidence = evidenceAll.TakeLast(MaximumCoalescedTurns - 1).ToArray();
        return latest with { EarlierEvidence = evidence, PartialCoverage = earlier.PartialCoverage || latest.PartialCoverage || evidenceAll.Length > evidence.Length };
    }

    static ProjectSummaryRequest FitBudget(ProjectSummaryRequest request)
    {
        var earlier = (request.EarlierEvidence ?? []).ToList();
        var fitted = request with { EarlierEvidence = earlier };
        while (!ProjectSummaryValidator.IsWithinInputBudget(fitted) && earlier.Count > 0)
        {
            earlier.RemoveAt(0);
            fitted = fitted with { EarlierEvidence = earlier.ToArray(), PartialCoverage = true };
        }
        var assistant = fitted.AssistantFinal;
        var user = fitted.UserRequest;
        var prior = fitted.PriorSummary;
        while (!ProjectSummaryValidator.IsWithinInputBudget(fitted) && assistant.Length > 200)
        {
            assistant = assistant[(assistant.Length - 100)..];
            fitted = fitted with { AssistantFinal = assistant, PartialCoverage = true };
        }
        while (!ProjectSummaryValidator.IsWithinInputBudget(fitted) && user is { Length: > 100 })
        {
            user = user[(user.Length - 50)..];
            fitted = fitted with { UserRequest = user, PartialCoverage = true };
        }
        while (!ProjectSummaryValidator.IsWithinInputBudget(fitted) && prior is { Length: > 100 })
        {
            prior = prior[(prior.Length - 50)..];
            fitted = fitted with { PriorSummary = prior, PartialCoverage = true };
        }
        if (!ProjectSummaryValidator.IsWithinInputBudget(fitted))
            fitted = fitted with { CandidateProjectNames = fitted.CandidateProjectNames.Take(10).ToArray(), PartialCoverage = true };
        return fitted;
    }
}

/// <summary>A request tagged so callers can reject stale worker results before persistence.</summary>
public sealed record ProjectSummaryWorkItem(ProjectSummaryRequest Request, long Generation);
