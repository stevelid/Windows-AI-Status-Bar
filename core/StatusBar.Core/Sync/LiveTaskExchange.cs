using System.Text.Json;
using StatusBar.Core.Common;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Sync;

/// <summary>The deliberately small, encrypted cross-PC exchange; no session references or tool data.</summary>
public sealed record LiveTaskSnapshot(int Version, string DeviceId, string Label, long Sequence,
    DateTimeOffset PublishedAt, IReadOnlyList<SharedLiveTask> Tasks);

/// <summary>Normalized display-only evidence from the computer executing a task.</summary>
public sealed record SharedLiveTask(string Id, AgentProvider Provider, string Title,
    AgentTaskStatus Status, StateConfidence Confidence, DateTimeOffset LastActivity);

/// <summary>A remote computer's last accepted snapshot and receiver-measured freshness.</summary>
public sealed record RemoteTaskDevice(string DeviceId, string Label, DateTimeOffset ReceivedAt,
    bool IsFresh, IReadOnlyList<AgentTask> Tasks);

/// <summary>Validates exchange data and excludes cloud tasks already collected on both PCs.</summary>
public static class LiveTaskExchange
{
    public const int MaximumTasks = 100;
    public const int MaximumBytes = 128 * 1024;

    /// <summary>Projects only bounded task display fields; raw provider state never crosses machines.</summary>
    public static LiveTaskSnapshot Create(string deviceId, string label, long sequence,
        DateTimeOffset publishedAt, IEnumerable<AgentTask> tasks) => new(1, deviceId,
        TextSanitizer.SanitizeTitleCandidate(label) ?? "PC", sequence, publishedAt,
        tasks.Where(task => !task.IsCloud && !task.IsRemote).Take(MaximumTasks)
            .Select(task => new SharedLiveTask(task.Id, task.Provider,
                TextSanitizer.SanitizeTitleCandidate(task.Title) ?? "AI task",
                task.Status, task.Confidence, task.LastActivity)).ToArray());

    /// <summary>Refuses malformed, oversized and incompatible snapshots as a whole.</summary>
    public static LiveTaskSnapshot Read(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("ExchangeTooLarge");
        var value = JsonSerializer.Deserialize<LiveTaskSnapshot>(bytes, new JsonSerializerOptions { MaxDepth = 12 });
        if (value is null || value.Version != 1 || !Guid.TryParseExact(value.DeviceId, "N", out _) ||
            value.Sequence <= 0 || value.Label is not { Length: > 0 and <= 80 } || value.Label.Any(char.IsControl) ||
            value.Tasks is null || value.Tasks.Count > MaximumTasks ||
            value.Tasks.Any(task => task is null || task.Id is not { Length: > 0 and <= 160 } ||
                task.Id.Any(char.IsControl) || task.Title is not { Length: > 0 and <= 100 } ||
                task.Title.Any(char.IsControl) || !Enum.IsDefined(task.Provider) ||
                !Enum.IsDefined(task.Status) || !Enum.IsDefined(task.Confidence)) ||
            value.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != value.Tasks.Count)
            throw new InvalidDataException("ExchangeInvalid");
        return value;
    }

    /// <summary>Adds remote counts to the UI only; local history, projects and collectors stay isolated.</summary>
    public static StatusBarState Attach(StatusBarState local, IReadOnlyList<RemoteTaskDevice> devices) => local with
    {
        RemoteDevices = devices,
        WorkingCount = local.WorkingCount + devices.SelectMany(device => device.Tasks).Count(task => task.Status == AgentTaskStatus.Working),
        AttentionCount = local.AttentionCount + devices.SelectMany(device => device.Tasks).Count(task => task.Status == AgentTaskStatus.NeedsAttention),
    };
}

/// <summary>Tracks heartbeat advancement by receiver time, independent of the other PC's clock.</summary>
public sealed class LiveTaskInbox(TimeProvider time)
{
    sealed record Entry(LiveTaskSnapshot Snapshot, DateTimeOffset ReceivedAt, long ReceivedStamp, bool Advanced);
    readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    /// <summary>Duplicates and reordered snapshots never refresh a heartbeat. Initial cached files are unconfirmed.</summary>
    public bool Accept(LiveTaskSnapshot snapshot)
    {
        var exists = _devices.TryGetValue(snapshot.DeviceId, out var old);
        if (exists && snapshot.Sequence <= old!.Snapshot.Sequence) return false;
        _devices[snapshot.DeviceId] = new(snapshot, time.GetUtcNow(), time.GetTimestamp(), exists);
        return true;
    }

    /// <summary>Builds read-only remote rows; loss of a heartbeat never implies completion.</summary>
    public IReadOnlyList<RemoteTaskDevice> Current() => _devices.Values
        .Where(entry => time.GetElapsedTime(entry.ReceivedStamp) < ForgetAfter)
        .Select(entry =>
        {
            var fresh = entry.Advanced && time.GetElapsedTime(entry.ReceivedStamp) < StaleAfter;
            return new RemoteTaskDevice(entry.Snapshot.DeviceId, entry.Snapshot.Label, entry.ReceivedAt, fresh,
                entry.Snapshot.Tasks.Select(task => new AgentTask
                {
                    Id = "remote:" + entry.Snapshot.DeviceId + ":" + task.Id,
                    Provider = task.Provider,
                    Title = TextSanitizer.SanitizeTitleCandidate(task.Title) ?? "AI task",
                    Status = fresh ? task.Status : AgentTaskStatus.Unknown,
                    Confidence = fresh ? task.Confidence : StateConfidence.Stale,
                    // Remote wall clocks are not evidence of receipt or liveness.
                    LastActivity = entry.ReceivedAt,
                    IsRemote = true,
                }).ToArray());
        }).OrderBy(device => device.Label, StringComparer.Ordinal).ToArray();
}
