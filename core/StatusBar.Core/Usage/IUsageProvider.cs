namespace StatusBar.Core.Usage;

/// <summary>Fetches normalized usage windows from one provider.</summary>
public interface IUsageProvider
{
    /// <summary>The provider whose usage this instance retrieves.</summary>
    UsageSource Source { get; }

    /// <summary>Fetches the provider's current quota windows.</summary>
    /// <exception cref="UsageAuthRequiredException">The provider requires a sign-in.</exception>
    /// <exception cref="UsageRateLimitedException">The provider has temporarily rate-limited requests.</exception>
    Task<IReadOnlyList<UsageWindow>> FetchAsync(CancellationToken cancellationToken);
}
