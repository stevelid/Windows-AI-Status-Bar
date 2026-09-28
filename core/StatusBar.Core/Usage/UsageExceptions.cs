namespace StatusBar.Core.Usage;

/// <summary>Indicates that a provider needs the user to sign in before usage can be fetched.</summary>
public sealed class UsageAuthRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Indicates that a provider temporarily refused requests because of rate limiting.</summary>
public sealed class UsageRateLimitedException(TimeSpan? retryAfter) : Exception("Rate limited")
{
    /// <summary>The provider's suggested delay before retrying, when it supplied one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
