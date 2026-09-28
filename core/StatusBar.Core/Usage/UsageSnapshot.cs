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
/// <remarks>Windows retain the last good values after a failed refresh; the list is empty before the first success.</remarks>
public sealed record UsageSnapshot(
    UsageSource Source,
    IReadOnlyList<UsageWindow> Windows,
    UsageHealth Health,
    DateTimeOffset? LastSuccess,
    string StatusCode);
