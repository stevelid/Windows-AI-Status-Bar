using StatusBar.Core.Tasks;

namespace StatusBar.Core.Projects;

/// <summary>Builds a stable project view from tasks, manual/candidate memberships and derived updates.</summary>
public static class ProjectOverviewBuilder
{
    public const int VisibleUpdatesPerProject = 2;

    /// <summary>Projects needing input or showing unseen work remain ahead of ordinary recent activity.</summary>
    public static ProjectOverviewState Build(
        IEnumerable<ProjectDefinition> projects,
        IEnumerable<ProjectMembership> memberships,
        IEnumerable<AgentTask> conversations,
        IEnumerable<ProjectUpdate> updates,
        IEnumerable<ProjectAcknowledgement> acknowledgements,
        IEnumerable<ProjectThreadReference>? threadReferences = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(acknowledgements);

        var projectMap = projects
            .Where(project => project is not null && IsValidProject(project))
            .GroupBy(project => project.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(project => project.Id, StringComparer.Ordinal);
        var tasks = conversations
            .Where(task => task is not null && !string.IsNullOrWhiteSpace(task.Id))
            .GroupBy(task => task.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(task => task.LastActivity).First())
            .ToDictionary(task => task.Id, StringComparer.Ordinal);
        var membershipsByThread = ResolveMemberships(memberships, projectMap, tasks);
        var acknowledged = acknowledgements
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.UpdateId) && item.Revision > 0)
            .Select(item => (item.UpdateId, item.Revision))
            .ToHashSet();
        var references = (threadReferences ?? []).Where(reference => reference is not null &&
                reference.ThreadId is { Length: > 0 and <= ProjectTextLimits.MaximumEvidenceIdLength } &&
                reference.DisplayTitle is { Length: > 0 and <= 120 } && Enum.IsDefined(reference.Provider))
            .GroupBy(reference => reference.ThreadId, StringComparer.Ordinal)
            .Select(group => group.First()).ToDictionary(reference => reference.ThreadId, StringComparer.Ordinal);
        var validUpdates = updates
            .Where(ProjectTextLimits.IsBounded)
            .Where(update => projectMap.ContainsKey(update.ProjectId) &&
                             membershipsByThread.TryGetValue(update.ThreadId, out var linked) &&
                             linked.Contains(update.ProjectId, StringComparer.Ordinal))
            .GroupBy(update => update.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(update => update.Revision).First())
            .ToArray();

        var items = new List<ProjectOverviewItem>();
        foreach (var project in projectMap.Values)
        {
            var memberTasks = membershipsByThread
                .Where(pair => pair.Value.Contains(project.Id, StringComparer.Ordinal))
                .Where(pair => tasks.ContainsKey(pair.Key))
                .Select(pair => tasks[pair.Key])
                .OrderByDescending(task => task.Status == AgentTaskStatus.NeedsAttention)
                .ThenByDescending(task => task.Status == AgentTaskStatus.Working)
                .ThenByDescending(task => task.LastActivity)
                .ToArray();
            var projectOwned = validUpdates.Where(update => update.ProjectId == project.Id).ToArray();
            // A question stays open only while it is the thread's newest update and Steve has not opened it;
            // otherwise a project would sit in "Needs you" forever after one question.
            var newestByThread = projectOwned.GroupBy(update => update.ThreadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(update => update.OccurredAt).ThenByDescending(update => update.Revision).First(),
                    StringComparer.Ordinal);
            bool IsOpenQuestion(ProjectUpdate update) => update.NeedsInput &&
                !acknowledged.Contains((update.Id, update.Revision)) &&
                newestByThread[update.ThreadId].Id == update.Id && newestByThread[update.ThreadId].Revision == update.Revision;
            var projectUpdates = projectOwned
                .OrderByDescending(IsOpenQuestion)
                .ThenByDescending(update => !acknowledged.Contains((update.Id, update.Revision)))
                .ThenByDescending(update => update.OccurredAt)
                .ToArray();
            var projectThreads = membershipsByThread
                .Where(pair => pair.Value.Contains(project.Id, StringComparer.Ordinal))
                .Select(pair => pair.Key)
                .Distinct(StringComparer.Ordinal);
            var conversationsForProject = projectThreads
                .Select(threadId => new ProjectConversation(
                    tasks.TryGetValue(threadId, out var task) ? task : RecoveredTask(threadId, projectUpdates, references.GetValueOrDefault(threadId)),
                    projectUpdates.Where(update => update.ThreadId == threadId).ToArray()))
                .Where(conversation => conversation.Updates.Count > 0 || tasks.ContainsKey(conversation.Task.Id))
                .ToArray();
            var unseenCount = projectUpdates.Count(update => !acknowledged.Contains((update.Id, update.Revision)));
            var activity = project.LastMeaningfulActivity ?? MaxActivity(memberTasks, projectUpdates);
            var effectiveProject = project with { LastMeaningfulActivity = activity };
            items.Add(new ProjectOverviewItem(
                effectiveProject,
                conversationsForProject,
                projectUpdates,
                memberTasks.Any(task => task.Status == AgentTaskStatus.Working),
                memberTasks.Any(task => task.Status == AgentTaskStatus.NeedsAttention) || projectUpdates.Any(IsOpenQuestion),
                unseenCount,
                Math.Max(0, projectUpdates.Length - VisibleUpdatesPerProject)));
        }

        var ordered = items
            .OrderByDescending(item => item.Project.IsPinned)
            .ThenByDescending(item => item.HasUnresolvedInput)
            .ThenByDescending(item => item.UnseenUpdateCount > 0)
            .ThenByDescending(item => item.HasWorkingConversation)
            .ThenByDescending(item => item.Project.LastMeaningfulActivity)
            .ThenBy(item => item.Project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groupedIds = membershipsByThread.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var ungrouped = tasks.Values
            .Where(task => !groupedIds.Contains(task.Id))
            .OrderByDescending(task => task.Status == AgentTaskStatus.NeedsAttention)
            .ThenByDescending(task => task.LastActivity)
            .ToArray();
        return new ProjectOverviewState(ordered, ungrouped);
    }

    static AgentTask RecoveredTask(string threadId, IReadOnlyList<ProjectUpdate> updates, ProjectThreadReference? reference)
    {
        var separator = threadId.IndexOf(':');
        var provider = reference?.Provider ?? (separator > 0 && string.Equals(threadId[..separator], "claude", StringComparison.Ordinal)
            ? AgentProvider.Claude : AgentProvider.Codex);
        return new AgentTask
        {
            Id = threadId,
            Provider = provider,
            Title = reference?.DisplayTitle ?? updates.FirstOrDefault(update => update.ThreadId == threadId)?.LatestAction ?? "AI conversation",
            Status = AgentTaskStatus.Unknown,
            Confidence = StateConfidence.Stale,
            LastActivity = new[] { reference?.LastActivity ?? DateTimeOffset.MinValue }
                .Concat(updates.Where(update => update.ThreadId == threadId).Select(update => update.OccurredAt)).Max(),
            SessionReference = separator >= 0 ? threadId[(separator + 1)..] : threadId,
        };
    }

    static Dictionary<string, string[]> ResolveMemberships(
        IEnumerable<ProjectMembership> memberships,
        IReadOnlyDictionary<string, ProjectDefinition> projects,
        IReadOnlyDictionary<string, AgentTask> tasks)
    {
        var accepted = memberships
            .Where(item => item is not null && item.ProjectId is not null &&
                           !string.IsNullOrWhiteSpace(item.ThreadId) &&
                           Enum.IsDefined(item.Source) && (item.IsUnassigned
                               ? item.Source == ProjectMembershipSource.Manual && item.ProjectId.Length == 0
                               : item.ProjectId.Length > 0 && projects.ContainsKey(item.ProjectId)))
            .GroupBy(item => item.ThreadId, StringComparer.Ordinal);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in accepted)
        {
            var entries = group.ToArray();
            var manual = entries.Where(item => item.Source == ProjectMembershipSource.Manual).ToArray();
            if (manual.Any(item => item.IsUnassigned))
            {
                result[group.Key] = [];
                continue;
            }
            var effective = manual.Length > 0
                ? manual
                : entries.Where(item => item.Source == ProjectMembershipSource.ExplicitReference).ToArray();
            if (effective.Length == 0) effective = entries;
            result[group.Key] = effective.Select(item => item.ProjectId).Distinct(StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    static bool IsValidProject(ProjectDefinition project) =>
        project.Id is { Length: > 0 and <= ProjectTextLimits.MaximumEvidenceIdLength } &&
        project.Name is { Length: > 0 and <= 120 };

    static DateTimeOffset? MaxActivity(IEnumerable<AgentTask> tasks, IEnumerable<ProjectUpdate> updates)
    {
        var values = tasks.Select(task => (DateTimeOffset?)task.LastActivity)
            .Concat(updates.Select(update => (DateTimeOffset?)update.OccurredAt));
        return values.Max();
    }
}
