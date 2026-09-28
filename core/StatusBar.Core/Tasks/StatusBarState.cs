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
    /// <summary>Tasks that finished within the "recently completed" window and are still listed.</summary>
    /// <remarks>Derived from <see cref="Tasks"/>, so it needs no extra constructor argument and
    /// expires with the tasks themselves.</remarks>
    public int DoneCount => Tasks.Count(task => task.Status == AgentTaskStatus.Complete);

    /// <summary>Tasks whose last turn failed within the "recently completed" window.</summary>
    public int FailedCount => Tasks.Count(task => task.Status == AgentTaskStatus.Failed);

    /// <summary>Creates an empty state with no configured providers.</summary>
    public static StatusBarState Empty { get; } = new(
        Array.Empty<AgentTask>(),
        0,
        0,
        new ReadOnlyDictionary<AgentProvider, ProviderHealth>(
            new Dictionary<AgentProvider, ProviderHealth>()));
}
