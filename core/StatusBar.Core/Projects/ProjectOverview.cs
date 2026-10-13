using StatusBar.Core.Tasks;

namespace StatusBar.Core.Projects;

/// <summary>A user-visible project grouping for one or more AI conversations.</summary>
public sealed record ProjectDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsPinned { get; init; }
    public DateTimeOffset? LastMeaningfulActivity { get; init; }
    public string? CurrentSummary { get; init; }
}

/// <summary>Associates one conversation with a project. A conversation may belong to several projects.</summary>
public sealed record ProjectMembership(string ProjectId, string ThreadId, ProjectMembershipSource Source, bool IsUnassigned = false);

/// <summary>Whether a project association is user-controlled or an evidence-based candidate.</summary>
public enum ProjectMembershipSource
{
    Suggested,
    ExplicitReference,
    Manual,
}

/// <summary>A concise update derived from one completed conversation turn.</summary>
public sealed record ProjectUpdate
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ThreadId { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string LatestAction { get; init; }
    public IReadOnlyList<string> Progress { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SourceEvidenceIds { get; init; } = Array.Empty<string>();
    public bool NeedsInput { get; init; }
}

/// <summary>The exact displayed revision that the user has opened and acknowledged.</summary>
public sealed record ProjectAcknowledgement(string UpdateId, int Revision);

/// <summary>Private, bounded navigation metadata retained so cached updates remain openable after restart.</summary>
public sealed record ProjectThreadReference(string ThreadId, AgentProvider Provider, string DisplayTitle, DateTimeOffset LastActivity);

/// <summary>A conversation and the updates attributed to it in one project.</summary>
public sealed record ProjectConversation(AgentTask Task, IReadOnlyList<ProjectUpdate> Updates);

/// <summary>Project card data. Working, unresolved input and unseen results are independent facts.</summary>
public sealed record ProjectOverviewItem(
    ProjectDefinition Project,
    IReadOnlyList<ProjectConversation> Conversations,
    IReadOnlyList<ProjectUpdate> Updates,
    bool HasWorkingConversation,
    bool HasUnresolvedInput,
    int UnseenUpdateCount,
    int OverflowUpdateCount);

/// <summary>Projects in display order plus conversations with no project assignment.</summary>
public sealed record ProjectOverviewState(
    IReadOnlyList<ProjectOverviewItem> Projects,
    IReadOnlyList<AgentTask> UngroupedConversations);

/// <summary>Bounds summary text before it enters project state or durable storage.</summary>
public static class ProjectTextLimits
{
    public const int MaximumActionLength = 160;
    public const int MaximumProgressItems = 4;
    public const int MaximumProgressLength = 120;
    public const int MaximumEvidenceIds = 12;
    public const int MaximumEvidenceIdLength = 128;

    /// <summary>Returns false when a model update exceeds the compact project display bounds.</summary>
    public static bool IsBounded(ProjectUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update.Id is { Length: > 0 and <= MaximumEvidenceIdLength } &&
               update.ProjectId is { Length: > 0 and <= MaximumEvidenceIdLength } &&
               update.ThreadId is { Length: > 0 and <= MaximumEvidenceIdLength } &&
               update.Revision > 0 &&
               update.LatestAction is { Length: > 0 and <= MaximumActionLength } &&
               update.Progress is not null && update.SourceEvidenceIds is not null &&
               update.Progress.Count <= MaximumProgressItems &&
               update.Progress.All(text => !string.IsNullOrWhiteSpace(text) && text.Length <= MaximumProgressLength) &&
               update.SourceEvidenceIds.Count <= MaximumEvidenceIds &&
               update.SourceEvidenceIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumEvidenceIdLength) &&
               update.SourceEvidenceIds.Distinct(StringComparer.Ordinal).Count() == update.SourceEvidenceIds.Count;
    }
}
