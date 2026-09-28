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
                .Select(bucket => new UsageWindow(
                    bucket.Key, bucket.Label, bucket.Utilization, bucket.ResetsAt, WindowLength(bucket.Key)))
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

    /// <summary>
    /// Maps Claude's bucket keys to window lengths: "session" (limits array) and "five_hour"
    /// (legacy) are the five-hour session; "weekly_*" and "seven_day*" are weekly.
    /// </summary>
    internal static TimeSpan? WindowLength(string key) => key switch
    {
        "session" or "five_hour" => TimeSpan.FromHours(5),
        _ when key.StartsWith("weekly", StringComparison.Ordinal) ||
               key.StartsWith("seven_day", StringComparison.Ordinal) => TimeSpan.FromDays(7),
        _ => null,
    };
}
