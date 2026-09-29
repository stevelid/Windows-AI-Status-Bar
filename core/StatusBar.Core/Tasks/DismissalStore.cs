using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StatusBar.Core.Tasks;

/// <summary>Stores dismissed task evidence in memory and, optionally, in an app-owned JSON file.</summary>
public sealed class DismissalStore
{
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    const long MaximumStateFileBytes = 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly object _gate = new();
    readonly Dictionary<string, DateTimeOffset> _expiresAt = new(StringComparer.Ordinal);
    readonly TimeProvider _time;
    readonly string? _filePath;
    JsonObject _document = new();
    bool _canWrite = true;

    /// <summary>Creates a dismissal store, optionally persisted to the given state file.</summary>
    public DismissalStore(TimeProvider time, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        Load();
    }

    internal bool Contains(string taskId, string evidenceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceKey);
        lock (_gate)
        {
            if (PruneExpired(_time.GetUtcNow())) Persist();
            return _expiresAt.ContainsKey(ComputeKey(taskId, evidenceKey));
        }
    }

    internal void Dismiss(string taskId, string evidenceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceKey);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneExpired(now);
            _expiresAt[ComputeKey(taskId, evidenceKey)] = now + Lifetime;
            Persist();
        }
    }

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
            if (!document.TryGetPropertyValue("dismissals", out var node)) return;
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
                    !ReadString(item, "expiresAt", out var expiresText) ||
                    !DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt) ||
                    expiresAt <= now)
                {
                    continue;
                }

                _expiresAt[key] = expiresAt;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Keep a malformed or unreadable state file intact; dismissals still work for this run.
            _canWrite = false;
        }
    }

    bool PruneExpired(DateTimeOffset now)
    {
        var expired = _expiresAt
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired) _expiresAt.Remove(key);
        return expired.Length > 0;
    }

    void Persist()
    {
        if (_filePath is null || !_canWrite) return;

        RefreshDocument();
        var entries = new JsonArray();
        foreach (var pair in _expiresAt.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            entries.Add(new JsonObject
            {
                ["key"] = pair.Key,
                ["expiresAt"] = pair.Value.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        _document["dismissals"] = entries;

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
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException)
        {
            // A persistence failure must not stop the user from dismissing a row in this run.
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
        if (value[propertyName] is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var found) || found is null)
            return false;
        result = found;
        return true;
    }
}
