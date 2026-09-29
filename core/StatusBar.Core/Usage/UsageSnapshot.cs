namespace StatusBar.Core.Usage;

/// <summary>Describes whether a provider's latest usage data is current and usable.</summary>
public enum UsageHealth
{
    Loading,
    Ok,
    Stale,
    SignedOut,
    Unavailable,
}

/// <summary>A provider's last known quota windows and refresh status.</summary>
/// <remarks>
/// Windows retain the last good values after a failed refresh; the list is empty before the first success.
/// <c>Runways</c> holds each window's rate-based projection by window key, computed at the last
/// successful refresh (empty until enough readings exist); it never applies the low-percentage rule,
/// which the view applies with the user's threshold.
/// </remarks>
public sealed record UsageSnapshot(
    UsageSource Source,
    IReadOnlyList<UsageWindow> Windows,
    UsageHealth Health,
    DateTimeOffset? LastSuccess,
    string StatusCode,
    string? ErrorType = null,
    IReadOnlyDictionary<string, UsageRunway>? Runways = null)
{
    /// <summary>The projection for a window, or <see cref="UsageRunway.Unknown"/> when none exists.</summary>
    public UsageRunway RunwayFor(UsageWindow window) =>
        window is not null && Runways is not null && Runways.TryGetValue(window.Key, out var runway)
            ? runway
            : UsageRunway.Unknown;
}
