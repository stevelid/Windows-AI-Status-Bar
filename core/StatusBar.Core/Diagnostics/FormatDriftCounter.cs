namespace StatusBar.Core.Diagnostics;

/// <summary>Counts malformed records and unfamiliar provider record types without retaining record content.</summary>
public sealed class FormatDriftCounter
{
    readonly object _gate = new();
    readonly Dictionary<string, long> _unknownBySignature = new(StringComparer.Ordinal);
    long _malformedCount;

    /// <summary>Gets the number of malformed JSONL records observed.</summary>
    public long MalformedCount
    {
        get { lock (_gate) return _malformedCount; }
    }

    /// <summary>Gets the total number of records with unfamiliar structural signatures.</summary>
    public long UnknownCount
    {
        get
        {
            lock (_gate)
                return _unknownBySignature.Values.Sum();
        }
    }

    /// <summary>Gets a snapshot of unfamiliar record counts, keyed by type names only.</summary>
    public IReadOnlyDictionary<string, long> UnknownBySignature
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, long>(_unknownBySignature, StringComparer.Ordinal);
        }
    }

    internal void RecordMalformed()
    {
        lock (_gate) _malformedCount++;
    }

    internal void RecordUnknown(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var safeSignature = string.Join('/', signature.Split('/').Select(NormalizeTypeName));
        lock (_gate)
        {
            _unknownBySignature.TryGetValue(safeSignature, out var count);
            _unknownBySignature[safeSignature] = count + 1;
        }
    }

    static string NormalizeTypeName(string value)
    {
        if (value.Length is 0 or > 48 || !char.IsAsciiLetter(value[0])) return "other";
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? value
            : "other";
    }
}
