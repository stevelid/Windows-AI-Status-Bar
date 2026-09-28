namespace StatusBar.Core.Tasks;

/// <summary>Availability of a task provider's latest evidence.</summary>
public enum ProviderHealthState
{
    Starting,
    Ok,
    Degraded,
    Unavailable,
}

/// <summary>Content-free health information for one task provider.</summary>
public sealed record ProviderHealth(
    ProviderHealthState State,
    string Code,
    DateTimeOffset? LastEvidence);
