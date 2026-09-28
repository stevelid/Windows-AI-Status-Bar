using StatusBar.Core.Usage;

namespace ClaudeUsageWidget.Providers;

/// <summary>Adapts Claude's usage service to the platform-neutral usage provider contract.</summary>
public sealed class ClaudeUsageProvider(UsageService service) : IUsageProvider
{
    /// <inheritdoc />
    public UsageSource Source => UsageSource.Claude;

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageWindow>> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buckets = await service.GetUsageAsync(cancellationToken);
            return buckets
                .Select(bucket => new UsageWindow(bucket.Key, bucket.Label, bucket.Utilization, bucket.ResetsAt))
                .ToArray();
        }
        catch (RateLimitedException ex)
        {
            throw new UsageRateLimitedException(ex.RetryAfter);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UsageAuthRequiredException("Claude sign-in is required.", ex);
        }
    }
}
