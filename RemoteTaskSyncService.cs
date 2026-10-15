using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using StatusBar.Core.Sync;
using StatusBar.Core.Tasks;

namespace ClaudeUsageWidget;

/// <summary>Shares encrypted task snapshots through an explicitly selected, trusted Drive folder.</summary>
internal sealed class RemoteTaskSyncService : IAsyncDisposable
{
    sealed record Identity(string Id, long Sequence, string PrivateKey, Dictionary<string, string> PinnedKeys);
    sealed record Advertisement(int Version, string DeviceId, string PublicKey);
    readonly string _directory;
    readonly string _identityPath;
    readonly string _label;
    readonly Func<StatusBarState> _local;
    readonly TimeProvider _time;
    readonly LiveTaskInbox _inbox;
    readonly SemaphoreSlim _pollGate = new(1, 1);
    readonly RSA _key;
    readonly ITimer _timer;
    Identity _identity;
    volatile bool _disposed;
    IReadOnlyList<RemoteTaskDevice> _current = [];
    string _status = "sync_waiting";

    internal event Action? Changed;
    internal IReadOnlyList<RemoteTaskDevice> Current => Volatile.Read(ref _current);
    internal string StatusKey => _status;

    internal RemoteTaskSyncService(string directory, string? label, Func<StatusBarState> local, TimeProvider time,
        string? identityDirectory = null, bool startPolling = true)
    {
        _directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        _identityPath = Path.Combine(identityDirectory ?? AppPaths.DataDir, "task-sync-identity.dat");
        _local = local;
        _time = time;
        _inbox = new LiveTaskInbox(time);
        _key = RSA.Create(2048);
        if (File.Exists(_identityPath))
        {
            // Refuse unreadable identity state instead of resetting its sequence or keys.
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_identityPath), null, DataProtectionScope.CurrentUser);
            _identity = JsonSerializer.Deserialize<Identity>(bytes) ?? throw new InvalidDataException("SyncIdentityInvalid");
            if (!Guid.TryParseExact(_identity.Id, "N", out _) || _identity.Sequence < 0 || _identity.PinnedKeys is null)
                throw new InvalidDataException("SyncIdentityInvalid");
            _key.ImportRSAPrivateKey(Convert.FromBase64String(_identity.PrivateKey), out _);
        }
        else
        {
            _identity = new(Guid.NewGuid().ToString("N"), 0, Convert.ToBase64String(_key.ExportRSAPrivateKey()), new());
            SaveIdentity();
        }
        _label = string.IsNullOrWhiteSpace(label) ? "PC " + _identity.Id[^4..] : label;
        _timer = time.CreateTimer(state => { _ = Task.Run(PollAsync); }, null,
            startPolling ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(15));
    }

    internal async Task PollAsync()
    {
        if (_disposed || !await _pollGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_disposed) return;
            if (!Directory.Exists(_directory)) throw new IOException("SyncFolderUnavailable");
            var self = new Advertisement(1, _identity.Id, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
            var selfPath = Path.Combine(_directory, _identity.Id + ".device");
            if (!File.Exists(selfPath)) AtomicWrite(selfPath, JsonSerializer.SerializeToUtf8Bytes(self));
            var peers = new List<Advertisement>();
            var rejected = false;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.device").Take(32))
            {
                try
                {
                    var ad = JsonSerializer.Deserialize<Advertisement>(ReadBounded(file, 4096));
                    if (ad is null || ad.Version != 1 || !Guid.TryParseExact(ad.DeviceId, "N", out _) ||
                        Path.GetFileName(file) != ad.DeviceId + ".device" || ad.DeviceId == _identity.Id) continue;
                    using var peerKey = RSA.Create();
                    peerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ad.PublicKey), out _);
                    if (peerKey.KeySize is < 2048 or > 4096) throw new InvalidDataException("SyncKeyInvalid");
                    var fingerprint = Convert.ToHexString(SHA256.HashData(peerKey.ExportSubjectPublicKeyInfo()));
                    if (_identity.PinnedKeys.TryGetValue(ad.DeviceId, out var pinned) && pinned != fingerprint)
                        throw new InvalidDataException("SyncKeyChanged");
                    if (!_identity.PinnedKeys.ContainsKey(ad.DeviceId))
                    {
                        // Opting into this private Drive folder enrols devices; pin their first keys locally.
                        _identity.PinnedKeys.Add(ad.DeviceId, fingerprint);
                        SaveIdentity();
                    }
                    peers.Add(ad);
                    if (peers.Count >= 8) break;
                }
                catch (Exception exception) when (IsExchangeFailure(exception)) { rejected = true; }
            }

            // Persist the counter before publishing, so a crash cannot replay an earlier sequence.
            _identity = _identity with { Sequence = checked(_identity.Sequence + 1) };
            SaveIdentity();
            var snapshot = LiveTaskExchange.Create(_identity.Id, _label, _identity.Sequence,
                _time.GetUtcNow(), _local().Tasks);
            foreach (var peer in peers)
            {
                try
                {
                    using var peerKey = RSA.Create();
                    peerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peer.PublicKey), out _);
                    var outbound = Path.Combine(_directory, _identity.Id + "." + peer.DeviceId + ".snapshot");
                    AtomicWrite(outbound, LiveTaskEncryption.Seal(snapshot, peer.DeviceId, _key, peerKey));
                    var inbound = Path.Combine(_directory, peer.DeviceId + "." + _identity.Id + ".snapshot");
                    if (File.Exists(inbound))
                        _inbox.Accept(LiveTaskEncryption.Open(ReadBounded(inbound, LiveTaskExchange.MaximumBytes * 2),
                            peer.DeviceId, _identity.Id, peerKey, _key));
                }
                catch (Exception exception) when (IsExchangeFailure(exception)) { rejected = true; }
            }
            _status = rejected ? "sync_retrying" : peers.Count == 0 ? "sync_waiting" : "sync_connected";
        }
        catch (Exception exception) when (IsExchangeFailure(exception) || exception is OverflowException)
        {
            _status = "sync_retrying";
        }
        finally
        {
            Volatile.Write(ref _current, _inbox.Current());
            _pollGate.Release();
            if (!_disposed) Changed?.Invoke();
        }
    }

    void SaveIdentity()
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_identity), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_identityPath)!);
        AtomicWrite(_identityPath, bytes);
    }

    static byte[] ReadBounded(string file, int maximum)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("SyncSizeInvalid");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    static void AtomicWrite(string file, byte[] bytes)
    {
        var pending = file + ".pending";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, file, overwrite: true);
    }

    static bool IsExchangeFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        JsonException or CryptographicException or FormatException or ArgumentException or NotSupportedException;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _pollGate.WaitAsync().ConfigureAwait(false);
        _key.Dispose();
        _pollGate.Release();
    }
}
