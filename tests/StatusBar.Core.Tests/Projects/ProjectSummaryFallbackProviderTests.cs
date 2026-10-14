using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Projects;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectSummaryFallbackProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");

    [Fact]
    public async Task Valid_primary_no_op_is_not_retried_through_fallback()
    {
        var primary = new StubProvider((_, _) => Task.FromResult<ProjectSummaryResult?>(new(null, "", [], [])));
        var fallback = new StubProvider((_, _) => throw new InvalidOperationException("fallback should not run"));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, new FakeTimeProvider(Now));

        var result = await provider.SummarizeAsync(Request(), CancellationToken.None);

        Assert.Equal("", result!.LatestAction);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task Transient_primary_failure_uses_fallback_and_opens_fifteen_minute_circuit()
    {
        var time = new FakeTimeProvider(Now);
        var primary = new StubProvider((_, _) => Task.FromException<ProjectSummaryResult?>(
            new ProjectSummaryProviderException(ProjectSummaryProviderFailureKind.RateLimited, "RateLimited")));
        var fallback = new StubProvider((request, _) => Task.FromResult<ProjectSummaryResult?>(ValidResult(request, "Luna result")));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, time);

        var first = await provider.SummarizeAsync(Request(), CancellationToken.None);
        time.Advance(ProjectSummaryFallbackProvider.PrimaryCircuitDuration - TimeSpan.FromTicks(1));
        var second = await provider.SummarizeAsync(Request("turn-2"), CancellationToken.None);

        Assert.Equal("Luna result", first!.LatestAction);
        Assert.Equal("Luna result", second!.LatestAction);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(2, fallback.CallCount);

        time.Advance(TimeSpan.FromTicks(1));
        primary.Handler = (request, _) => Task.FromResult<ProjectSummaryResult?>(ValidResult(request, "Haiku result"));
        var recovered = await provider.SummarizeAsync(Request("turn-3"), CancellationToken.None);
        Assert.Equal("Haiku result", recovered!.LatestAction);
        Assert.Equal(2, primary.CallCount);
        Assert.Equal(2, fallback.CallCount);
    }

    [Fact]
    public async Task Invalid_primary_output_uses_fallback()
    {
        var primary = new StubProvider((_, _) => Task.FromResult<ProjectSummaryResult?>(new("Bad", "Unsupported", [], ["other-turn:assistant"])));
        var fallback = new StubProvider((request, _) => Task.FromResult<ProjectSummaryResult?>(ValidResult(request, "Fallback")));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, new FakeTimeProvider(Now));

        var result = await provider.SummarizeAsync(Request(), CancellationToken.None);

        Assert.Equal("Fallback", result!.LatestAction);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new StubProvider((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ProjectSummaryResult?>(token);
        });
        var fallback = new StubProvider((_, _) => throw new InvalidOperationException("fallback should not run"));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, new FakeTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SummarizeAsync(Request(), cancellation.Token));
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task Unclassified_primary_failure_does_not_use_fallback()
    {
        var primary = new StubProvider((_, _) => Task.FromException<ProjectSummaryResult?>(new InvalidOperationException("Other")));
        var fallback = new StubProvider((_, _) => throw new InvalidOperationException("fallback should not run"));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, new FakeTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SummarizeAsync(Request(), CancellationToken.None));
        Assert.Equal(0, fallback.CallCount);
    }

    static ProjectSummaryRequest Request(string id = "turn-1") => new(
        "claude:fictional-thread", id, "Fictional conversation", "Please summarize a sample.",
        "Prepared one fictional result.", [], PartialCoverage: true, OccurredAt: Now);

    [Fact]
    public async Task Invalid_fallback_is_attempted_once_not_caught_again_as_a_primary_failure()
    {
        var primary = new StubProvider((_, _) => Task.FromResult<ProjectSummaryResult?>(null));
        var fallback = new StubProvider((_, _) => Task.FromException<ProjectSummaryResult?>(new InvalidDataException("Invalid")));
        var provider = new ProjectSummaryFallbackProvider(primary, fallback, new FakeTimeProvider(Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SummarizeAsync(Request(), CancellationToken.None));
        Assert.Equal(1, fallback.CallCount);
    }

    static ProjectSummaryResult ValidResult(ProjectSummaryRequest request, string action) =>
        new("Fictional project", action, [], [$"{request.EvidenceId}:assistant"]);

    sealed class StubProvider(Func<ProjectSummaryRequest, CancellationToken, Task<ProjectSummaryResult?>> handler) : IProjectSummaryProvider
    {
        public Func<ProjectSummaryRequest, CancellationToken, Task<ProjectSummaryResult?>> Handler { get; set; } = handler;
        public int CallCount { get; private set; }

        public Task<ProjectSummaryResult?> SummarizeAsync(ProjectSummaryRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Handler(request, cancellationToken);
        }
    }
}
