namespace StatusBar.Core.Projects;

/// <summary>Uses a subscription-backed fallback when the preferred project summary reader is unavailable.</summary>
public sealed class ProjectSummaryFallbackProvider(
    IProjectSummaryProvider primary,
    IProjectSummaryProvider fallback,
    TimeProvider timeProvider) : IProjectSummaryProvider
{
    public static readonly TimeSpan PrimaryCircuitDuration = TimeSpan.FromMinutes(15);

    readonly object _gate = new();
    DateTimeOffset _primaryUnavailableUntil;

    /// <inheritdoc />
    public async Task<ProjectSummaryResult?> SummarizeAsync(ProjectSummaryRequest request, CancellationToken cancellationToken)
    {
        var useFallback = false;
        lock (_gate) useFallback = _primaryUnavailableUntil > timeProvider.GetUtcNow();
        if (useFallback) return await RunFallbackAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await primary.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!ProjectSummaryValidator.TryValidate(request, result))
            {
                OpenCircuit();
            }
            else
            {
                lock (_gate) _primaryUnavailableUntil = default;
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            OpenCircuit();
        }
        catch (ProjectSummaryProviderException exception) when (exception.ShouldFallback)
        {
            OpenCircuit();
        }
        catch (InvalidDataException)
        {
            OpenCircuit();
        }
        return await RunFallbackAsync(request, cancellationToken).ConfigureAwait(false);
    }

    void OpenCircuit()
    {
        lock (_gate) _primaryUnavailableUntil = timeProvider.GetUtcNow() + PrimaryCircuitDuration;
    }

    async Task<ProjectSummaryResult?> RunFallbackAsync(ProjectSummaryRequest request, CancellationToken cancellationToken)
    {
        var result = await fallback.SummarizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ProjectSummaryValidator.TryValidate(request, result)) throw new InvalidDataException("FallbackSummaryOutputInvalid");
        return result;
    }
}

/// <summary>Failure categories that allow a project-summary provider to use its configured fallback.</summary>
public enum ProjectSummaryProviderFailureKind
{
    Unavailable,
    Timeout,
    RateLimited,
    InvalidOutput,
    Other,
}

/// <summary>A content-free project-summary provider failure suitable for fallback decisions.</summary>
public sealed class ProjectSummaryProviderException(
    ProjectSummaryProviderFailureKind kind,
    string code) : Exception(code)
{
    public ProjectSummaryProviderFailureKind Kind { get; } = kind;

    /// <summary>Whether the failure is transient or an invalid provider response that should use fallback.</summary>
    public bool ShouldFallback => Kind is ProjectSummaryProviderFailureKind.Unavailable or
        ProjectSummaryProviderFailureKind.Timeout or ProjectSummaryProviderFailureKind.RateLimited or
        ProjectSummaryProviderFailureKind.InvalidOutput;
}
