namespace StatusBar.Core.Tasks;

/// <summary>A provider's normalized view of its current AI tasks.</summary>
public sealed record ProviderTaskSnapshot(
    AgentProvider Provider,
    IReadOnlyList<AgentTask> Tasks,
    ProviderHealth Health);

/// <summary>Produces normalized task state for one desktop AI provider.</summary>
public interface IAgentTaskProvider : IAsyncDisposable
{
    /// <summary>The provider represented by this instance.</summary>
    AgentProvider Provider { get; }

    /// <summary>Raised when the provider's normalized view changes; snapshots contain no prompt text.</summary>
    event Action<ProviderTaskSnapshot>? Changed;

    /// <summary>Starts watching and periodic reconciliation.</summary>
    void Start();

    /// <summary>Forces discovery and reconciliation now.</summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>The latest immutable provider snapshot.</summary>
    ProviderTaskSnapshot Current { get; }
}
