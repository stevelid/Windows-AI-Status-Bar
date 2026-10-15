namespace StatusBar.Core.Projects;

/// <summary>The triage bucket a project row is listed under, in display order.</summary>
public enum ProjectSection
{
    NeedsYou,
    Working,
    New,
    Earlier,
}

/// <summary>Sorts projects into attention-ordered sections so the list answers "what needs me?" first.</summary>
public static class ProjectTriage
{
    /// <summary>Unresolved input outranks work in progress, which outranks unseen results.</summary>
    public static ProjectSection SectionOf(ProjectOverviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.HasUnresolvedInput) return ProjectSection.NeedsYou;
        if (item.HasWorkingConversation) return ProjectSection.Working;
        return item.UnseenUpdateCount > 0 ? ProjectSection.New : ProjectSection.Earlier;
    }

    /// <summary>Returns every section in fixed order; the caller's order is kept inside each section because the pane ranks stably.</summary>
    public static IReadOnlyList<(ProjectSection Section, IReadOnlyList<ProjectOverviewItem> Items)> Group(
        IReadOnlyList<ProjectOverviewItem> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return Enum.GetValues<ProjectSection>()
            .Select(section => (section, (IReadOnlyList<ProjectOverviewItem>)projects
                .Where(item => SectionOf(item) == section).ToArray()))
            .ToArray();
    }

    /// <summary>The one-line "latest action" for a project: a question for Steve first, otherwise the newest update.</summary>
    public static ProjectUpdate? LatestUpdate(ProjectOverviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.HasUnresolvedInput)
        {
            // Only a question that is still the newest in its thread is "open"; Updates is ordered open-questions-first.
            var open = item.Updates.Where(update => update.NeedsInput && item.Updates
                .Where(other => other.ThreadId == update.ThreadId)
                .All(other => other.OccurredAt < update.OccurredAt || (other.OccurredAt == update.OccurredAt && other.Revision <= update.Revision)))
                .OrderByDescending(update => update.OccurredAt).FirstOrDefault();
            if (open is not null) return open;
        }
        return item.Updates.OrderByDescending(update => update.OccurredAt).FirstOrDefault();
    }

    /// <summary>The same choice over any subset of updates, such as one conversation's.</summary>
    public static ProjectUpdate? LatestUpdate(IEnumerable<ProjectUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var newest = updates.OrderByDescending(update => update.OccurredAt).ThenByDescending(update => update.Revision).FirstOrDefault();
        return newest;
    }
}
