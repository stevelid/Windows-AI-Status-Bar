namespace StatusBar.Core.IO;

/// <summary>Coalesces JSONL filesystem changes and surfaces watcher overflow for full reconciliation.</summary>
internal sealed class DirectoryWatcher : IDisposable
{
    readonly object _gate = new();
    readonly TimeSpan _debounce;
    readonly Action _onChanged;
    readonly Action _onOverflow;
    readonly FileSystemWatcher _watcher;
    readonly ITimer _timer;
    bool _disposed;
    int _overflowCount;

    internal DirectoryWatcher(
        string path,
        TimeProvider time,
        TimeSpan debounce,
        Action onChanged,
        Action onOverflow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(onOverflow);
        if (debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));

        _debounce = debounce;
        _onChanged = onChanged;
        _onOverflow = onOverflow;
        _watcher = new FileSystemWatcher(path, "*.jsonl")
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _timer = time.CreateTimer(OnDebounce, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    internal int OverflowCount => Volatile.Read(ref _overflowCount);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsJsonl(e.Name)) Schedule();
    }

    void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsJsonl(e.Name) || IsJsonl(e.OldName)) Schedule();
    }

    void OnError(object sender, ErrorEventArgs e)
    {
        Interlocked.Increment(ref _overflowCount);
        try { _onOverflow(); }
        catch (Exception) { }
        Schedule();
    }

    void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void OnDebounce(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try { _onChanged(); }
        catch (Exception) { }
    }

    static bool IsJsonl(string? name) =>
        !string.IsNullOrWhiteSpace(name) && string.Equals(Path.GetExtension(name), ".jsonl", StringComparison.OrdinalIgnoreCase);
}
