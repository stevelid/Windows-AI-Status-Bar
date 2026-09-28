using System.Collections.ObjectModel;

namespace StatusBar.Core.Tasks;

/// <summary>Immutable task state prepared for the status bar and details pane.</summary>
/// <remarks>Tasks are ordered by attention, working, unknown, then recently completed or failed.</remarks>
public sealed record StatusBarState(
    IReadOnlyList<AgentTask> Tasks,
    int WorkingCount,
    int AttentionCount,
    IReadOnlyDictionary<AgentProvider, ProviderHealth> TaskProviderHealth)
{
    /// <summary>Creates an empty state with no configured providers.</summary>
    public static StatusBarState Empty { get; } = new(
        Array.Empty<AgentTask>(),
        0,
        0,
        new ReadOnlyDictionary<AgentProvider, ProviderHealth>(
            new Dictionary<AgentProvider, ProviderHealth>()));
}
