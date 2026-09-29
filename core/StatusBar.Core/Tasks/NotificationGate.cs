using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StatusBar.Core.Tasks;

/// <summary>De-duplicates attention notifications for each task evidence key.</summary>
/// <remarks>
/// Only a digest of the task and evidence identifiers is persisted. Existing state-file
/// sections are retained so this can share the file with <see cref="DismissalStore"/>.
/// </remarks>
public sealed class NotificationGate
{
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    const long MaximumStateFileBytes = 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly object _gate = new();
    readonly Dictionary<string, DateTimeOffset> _notifiedAt = new(StringComparer.Ordinal);
    readonly TimeProvider _time;
    readonly string? _filePath;
    JsonObject _document = new();
    bool _canWrite = true;

    /// <summary>Creates a gate with optional persistence in the app state file.</summary>
    public NotificationGate(TimeProvider time, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        Load();
    }

    /// <summary>
    /// Seeds attention already visible at startup. Seeded evidence is shown in the pane but
    /// does not produce a notification until a new evidence key arrives.
    /// </summary>
    public void Seed(IEnumerable<AgentTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var changed = PruneExpired(now);
            foreach (var task in tasks)
            {
                if (task is null) continue;
                if (task.Status == AgentTaskStatus.NeedsAttention)
                    changed |= _notifiedAt.TryAdd(ComputeKey(task.Id, EvidenceKeyFor(task)), now);
                if (task.Status is AgentTaskStatus.Complete or AgentTaskStatus.Failed)
                    changed |= _notifiedAt.TryAdd(ComputeKey(task.Id, CompletionKeyFor(task)), now);
            }

            if (changed) Persist();
        }
    }

    /// <summary>Claims a new attention evidence key for notification.</summary>
    public bool ShouldNotify(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != AgentTaskStatus.NeedsAttention) return false;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var changed = PruneExpired(now);
            var key = ComputeKey(task.Id, EvidenceKeyFor(task));
            if (_notifiedAt.ContainsKey(key))
            {
                if (changed) Persist();
                return false;
            }

            _notifiedAt[key] = now;
            Persist();
            return true;
        }
    }

    /// <summary>Claims a newly observed terminal task state for notification.</summary>
    public bool ShouldNotifyCompletion(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status is not (AgentTaskStatus.Complete or AgentTaskStatus.Failed)) return false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var changed = PruneExpired(now);
            var key = ComputeKey(task.Id, CompletionKeyFor(task));
            if (_notifiedAt.ContainsKey(key))
            {
                if (changed) Persist();
                return false;
            }
            _notifiedAt[key] = now;
            Persist();
            return true;
        }
    }

    static string CompletionKeyFor(AgentTask task) =>
        "completion:" + task.Status + ":" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    void Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;

        try
        {
            if (new FileInfo(_filePath).Length > MaximumStateFileBytes)
            {
                _canWrite = false;
                return;
            }

            if (JsonNode.Parse(File.ReadAllText(_filePath)) is not JsonObject document)
            {
                _canWrite = false;
                return;
            }

            _document = document;
            if (!document.TryGetPropertyValue("notifications", out var node)) return;
            if (node is not JsonArray entries)
            {
                _canWrite = false;
                return;
            }

            var now = _time.GetUtcNow();
            foreach (var item in entries.OfType<JsonObject>())
            {
                if (!ReadString(item, "key", out var key) ||
                    key.Length != 64 || !key.All(Uri.IsHexDigit) ||
                    !ReadString(item, "notifiedAt", out var notifiedText) ||
                    !DateTimeOffset.TryParse(
                        notifiedText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var notifiedAt) ||
                    notifiedAt + Lifetime <= now)
                {
                    continue;
                }

                _notifiedAt[key] = notifiedAt;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException)
        {
            // Keep an unreadable state file intact; the gate still works for this run.
            _canWrite = false;
        }
    }

    bool PruneExpired(DateTimeOffset now)
    {
        var expired = _notifiedAt
            .Where(pair => pair.Value + Lifetime <= now)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired) _notifiedAt.Remove(key);
        return expired.Length > 0;
    }

    void Persist()
    {
        if (_filePath is null || !_canWrite) return;

        RefreshDocument();
        var entries = new JsonArray();
        foreach (var pair in _notifiedAt.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            entries.Add(new JsonObject
            {
                ["key"] = pair.Key,
                ["notifiedAt"] = pair.Value.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        _document["notifications"] = entries;
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        var temporaryPath = Path.Combine(directory, "state." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, _document.ToJsonString(JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or JsonException or InvalidOperationException)
        {
            // Notification persistence is best effort and must not stop task collection.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    void RefreshDocument()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        try
        {
            if (new FileInfo(_filePath).Length <= MaximumStateFileBytes &&
                JsonNode.Parse(File.ReadAllText(_filePath)) is JsonObject document)
                _document = document;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    static string EvidenceKeyFor(AgentTask task) =>
        !string.IsNullOrWhiteSpace(task.EvidenceKey)
            ? task.EvidenceKey
            : task.Status + ":" + task.LastActivity.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

    static string ComputeKey(string taskId, string evidenceKey)
    {
        var taskBytes = Encoding.UTF8.GetBytes(taskId);
        var evidenceBytes = Encoding.UTF8.GetBytes(evidenceKey);
        var evidenceOffset = sizeof(int) + taskBytes.Length + sizeof(int);
        var material = new byte[evidenceOffset + evidenceBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(material, taskBytes.Length);
        taskBytes.CopyTo(material, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(sizeof(int) + taskBytes.Length), evidenceBytes.Length);
        evidenceBytes.CopyTo(material, evidenceOffset);
        return Convert.ToHexString(SHA256.HashData(material));
    }

    static bool ReadString(JsonObject value, string propertyName, out string result)
    {
        result = string.Empty;
        if (value[propertyName] is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var found) ||
            found is null)
            return false;
        result = found;
        return true;
    }
}
