using StatusBar.Core.Usage;

namespace ClaudeUsageWidget.Providers;

/// <summary>Adapts the Codex app-server usage service to the Core provider contract.</summary>
/// <remarks>The service is resolved per fetch so a changed Codex path can replace its process client.</remarks>
public sealed class CodexUsageProvider(Func<ChatGptUsageService> getService) : IUsageProvider
{
    /// <inheritdoc />
    public UsageSource Source => UsageSource.Codex;

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageWindow>> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buckets = await getService().GetUsageAsync(cancellationToken);
            return buckets
                .Select(bucket => new UsageWindow(
                    bucket.Key, bucket.Label, bucket.Utilization, bucket.ResetsAt, WindowLength(bucket.Key)))
                .ToArray();
        }
        catch (ChatGptSignInRequiredException ex)
        {
            throw new UsageAuthRequiredException("ChatGPT sign-in is required.", ex);
        }
    }

    /// <summary>
    /// Reads the window length from the key built by <c>ChatGptUsageParser</c>
    /// ("chatgpt_{rank}_{minutes}"); 0 minutes means Codex did not report a length.
    /// </summary>
    internal static TimeSpan? WindowLength(string key)
    {
        var lastSeparator = key.LastIndexOf('_');
        return lastSeparator >= 0 &&
               int.TryParse(key.AsSpan(lastSeparator + 1), out var minutes) &&
               minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }
}
