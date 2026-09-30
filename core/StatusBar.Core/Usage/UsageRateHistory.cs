using System.Text;
using System.Text.Json;

namespace StatusBar.Core.Usage;

/// <summary>How fast a window was used in one slice of time.</summary>
/// <param name="At">Middle of the slice.</param>
/// <param name="PerHour">Percentage points of allowance used per hour, lightly smoothed; null when nothing was recorded then.</param>
public readonly record struct RatePoint(DateTimeOffset At, double? PerHour);

/// <summary>A window's recent burn rate, ready to draw.</summary>
/// <param name="Points">One point per slice, oldest first, ending at the time the series was read.</param>
/// <param name="PeakPerHour">The highest smoothed rate, or null when there is no data.</param>
/// <param name="PeakAt">When the peak was.</param>
/// <param name="Resets">Times the window reset (its allowance rose).</param>
/// <param name="HasData">Whether any slice has a reading.</param>
public sealed record UsageRateSeries(
    IReadOnlyList<RatePoint> Points,
    double? PeakPerHour,
    DateTimeOffset? PeakAt,
    IReadOnlyList<DateTimeOffset> Resets,
    bool HasData);

/// <summary>
/// Keeps the recent readings of each session-length quota window and turns them into a burn-rate series
/// for the details pane. Readings are percentages and times only, never any content. They stay in memory
/// and, while persistence is on, also in an app-owned file so the chart survives a restart.
/// </summary>
/// <remarks>
/// Readings are whole percentages taken about once a minute or two, so a single slice is coarse: the
/// series uses 6-minute slices and a three-slice average, which shows bursts of activity without
/// pretending to more precision than the data has.
/// </remarks>
public sealed class UsageRateHistory
{
    /// <summary>How far back the chart looks.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromHours(2);

    /// <summary>Length of one slice of the chart.</summary>
    public static readonly TimeSpan Bucket = TimeSpan.FromMinutes(6);

    /// <summary>How long readings are kept; longer than <see cref="Span"/> so a restart loses nothing visible.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(3);

    /// <summary>Readings further apart than this are a gap (the app was closed or the provider unreachable), not a quiet spell.</summary>
    public static readonly TimeSpan GapAfter = TimeSpan.FromMinutes(15);

    /// <summary>Only windows up to this long are recorded (the five-hour sessions); weekly limits barely move in two hours.</summary>
    public static readonly TimeSpan MaximumWindowLength = TimeSpan.FromHours(6);

    /// <summary>A five-hour window used at an even pace loses this many percentage points an hour.</summary>
    public const double EvenPacePerHour = 20;

    // Same rule as the burn tracker: a rise larger than this between readings means the window reset.
    const double ResetRise = 1.0;
    const int MaximumReadingsPerWindow = 400;
    const long MaximumFileBytes = 1024 * 1024;
    static readonly TimeSpan MinimumWriteInterval = TimeSpan.FromSeconds(30);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    readonly object _gate = new();
    readonly Dictionary<(UsageSource Source, string Key), List<Reading>> _readings = new();
    readonly TimeProvider _time;
    readonly string _filePath;
    bool _persist;
    DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    /// <summary>Creates a history; <paramref name="filePath"/> is only used while persistence is on.</summary>
    public UsageRateHistory(TimeProvider time, string filePath, bool persist)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _time = time;
        _filePath = Path.GetFullPath(filePath);
        _persist = persist;
        if (persist) Load();
    }

    /// <summary>Whether readings are also stored in the history file.</summary>
    public bool IsPersistent
    {
        get { lock (_gate) return _persist; }
    }

    /// <summary>Turns file storage on (loading and writing) or off (deleting the file).</summary>
    public void SetPersistence(bool persist)
    {
        lock (_gate)
        {
            if (_persist == persist) return;
            _persist = persist;
            if (persist)
            {
                Load();
                Save(force: true);
            }
            else
            {
                DeleteFile();
            }
        }
    }

    /// <summary>Records one successful refresh: the remaining allowance of each session-length window.</summary>
    public void Record(UsageSource source, IReadOnlyList<UsageWindow> windows, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(windows);
        lock (_gate)
        {
            var changed = false;
            foreach (var window in windows)
            {
                if (window is null || string.IsNullOrEmpty(window.Key)) continue;
                if (window.Length is not TimeSpan length || length <= TimeSpan.Zero || length > MaximumWindowLength) continue;

                if (!_readings.TryGetValue((source, window.Key), out var list))
                {
                    list = new List<Reading>();
                    _readings[(source, window.Key)] = list;
                }

                // A repeat of the same moment (or an earlier one) adds nothing.
                if (list.Count > 0 && list[^1].At >= at) continue;
                list.Add(new Reading(at, window.RemainingPercent));
                Prune(list, at);
                changed = true;
            }

            if (changed) Save(force: false);
        }
    }

    /// <summary>The recent readings of one window (oldest first), for attributing use to conversations; empty when none.</summary>
    public IReadOnlyList<(DateTimeOffset At, double Remaining)> Readings(UsageSource source, string windowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);
        lock (_gate)
        {
            return _readings.TryGetValue((source, windowKey), out var list)
                ? list.Select(reading => (reading.At, reading.Remaining)).ToArray()
                : Array.Empty<(DateTimeOffset, double)>();
        }
    }

    /// <summary>The burn-rate series for one window, ending now; null when the window has never been recorded.</summary>
    public UsageRateSeries? Series(UsageSource source, string windowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);
        Reading[] readings;
        lock (_gate)
        {
            if (!_readings.TryGetValue((source, windowKey), out var list) || list.Count == 0) return null;
            readings = list.ToArray();
        }

        return Compute(readings, _time.GetUtcNow());
    }

    /// <summary>Builds the series from readings (oldest first). Exposed for tests.</summary>
    internal static UsageRateSeries Compute(IReadOnlyList<(DateTimeOffset At, double Remaining)> readings, DateTimeOffset now) =>
        Compute(readings.Select(reading => new Reading(reading.At, reading.Remaining)).ToArray(), now);

    static UsageRateSeries Compute(Reading[] readings, DateTimeOffset now)
    {
        var buckets = (int)(Span / Bucket);
        var start = now - Span;
        var drop = new double[buckets];
        var covered = new bool[buckets];
        var resets = new List<DateTimeOffset>();

        for (var i = 1; i < readings.Length; i++)
        {
            var before = readings[i - 1];
            var after = readings[i];
            if (after.At <= start || before.At > now) continue;
            if (after.At - before.At > GapAfter) continue;

            var middle = before.At + (after.At - before.At) / 2;
            var index = (int)Math.Floor((middle - start) / Bucket);
            if (index < 0 || index >= buckets) continue;

            covered[index] = true;
            var change = before.Remaining - after.Remaining;
            if (change < -ResetRise)
            {
                if (after.At > start) resets.Add(after.At);
            }
            else if (change > 0)
            {
                drop[index] += change;
            }
        }

        var raw = new double?[buckets];
        for (var i = 0; i < buckets; i++)
            raw[i] = covered[i] ? drop[i] / Bucket.TotalHours : null;

        var points = new RatePoint[buckets];
        double? peak = null;
        DateTimeOffset? peakAt = null;
        for (var i = 0; i < buckets; i++)
        {
            var at = start + Bucket * i + Bucket / 2;
            double? smoothed = null;
            if (raw[i] is not null)
            {
                var neighbours = new[] { i - 1, i, i + 1 }
                    .Where(index => index >= 0 && index < buckets && raw[index] is not null)
                    .Select(index => raw[index]!.Value)
                    .ToArray();
                smoothed = neighbours.Average();
            }

            points[i] = new RatePoint(at, smoothed);
            if (smoothed is double value && (peak is null || value > peak))
            {
                peak = value;
                peakAt = at;
            }
        }

        return new UsageRateSeries(points, peak, peakAt, resets, points.Any(point => point.PerHour is not null));
    }

    static void Prune(List<Reading> list, DateTimeOffset now)
    {
        var oldest = now - Retention;
        var drop = 0;
        while (drop < list.Count && list[drop].At < oldest) drop++;
        if (list.Count - drop > MaximumReadingsPerWindow) drop = list.Count - MaximumReadingsPerWindow;
        if (drop > 0) list.RemoveRange(0, drop);
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_filePath) || new FileInfo(_filePath).Length > MaximumFileBytes) return;
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_filePath, Encoding.UTF8));
            if (stored?.Windows is null) return;
            var now = _time.GetUtcNow();
            foreach (var window in stored.Windows)
            {
                if (string.IsNullOrWhiteSpace(window.Key) || !Enum.IsDefined(window.Source) || window.Readings is null) continue;
                var list = new List<Reading>();
                foreach (var pair in window.Readings)
                {
                    if (pair.Length != 2) continue;
                    var at = DateTimeOffset.FromUnixTimeSeconds((long)pair[0]);
                    var remaining = Math.Clamp(pair[1], 0, 100);
                    if (now - at >= Retention || at > now + TimeSpan.FromMinutes(5)) continue;
                    if (list.Count > 0 && list[^1].At >= at) continue;
                    list.Add(new Reading(at, remaining));
                }

                if (list.Count > 0) _readings[(window.Source, window.Key)] = list;
            }
        }
        catch (Exception)
        {
            // A damaged history file is ignored and replaced on the next save.
        }
    }

    void Save(bool force)
    {
        if (!_persist) return;
        var now = _time.GetUtcNow();
        if (!force && now - _lastWrite < MinimumWriteInterval) return;
        _lastWrite = now;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var stored = new StoredFile
            {
                Windows = _readings
                    .Where(pair => pair.Value.Count > 0)
                    .Select(pair => new StoredWindow
                    {
                        Source = pair.Key.Source,
                        Key = pair.Key.Key,
                        Readings = pair.Value
                            .Select(reading => new[] { (double)reading.At.ToUnixTimeSeconds(), Math.Round(reading.Remaining, 2) })
                            .ToList(),
                    })
                    .ToList(),
            };
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // The chart is a convenience; a write failure must not affect usage monitoring.
        }
    }

    void DeleteFile()
    {
        try
        {
            File.Delete(_filePath);
            File.Delete(_filePath + ".tmp");
        }
        catch (Exception)
        {
            // Nothing to do if the file is locked or already gone.
        }
    }

    readonly record struct Reading(DateTimeOffset At, double Remaining);

    sealed class StoredFile
    {
        public List<StoredWindow>? Windows { get; set; }
    }

    sealed class StoredWindow
    {
        public UsageSource Source { get; set; }
        public string Key { get; set; } = "";
        public List<double[]>? Readings { get; set; }
    }
}
