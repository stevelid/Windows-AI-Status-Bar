namespace StatusBar.Core.Projects;

/// <summary>Versioned durable project state. Text fields contain only derived summaries, never source transcripts.</summary>
public sealed record ProjectOverviewDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public List<ProjectDefinition> Projects { get; init; } = [];
    public List<ProjectMembership> Memberships { get; init; } = [];
    public List<ProjectUpdate> Updates { get; init; } = [];
    public List<ProjectAcknowledgement> Acknowledgements { get; init; } = [];
    public List<ProjectCoverageCheckpoint> Coverage { get; init; } = [];
    public List<ProjectThreadReference> ThreadReferences { get; init; } = [];
}

/// <summary>Last successfully summarised source turn for one project/conversation association.</summary>
public sealed record ProjectCoverageCheckpoint(
    string? ProjectId,
    string ThreadId,
    string TurnId,
    DateTimeOffset CoveredAt,
    string SchemaVersion,
    string PromptVersion,
    string Model);

/// <summary>Prunes and deletes project state without leaving acknowledgement or coverage orphans.</summary>
public static class ProjectOverviewDocumentRules
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Removes old updates unless they are pinned, unresolved or belong to a live conversation.</summary>
    public static ProjectOverviewDocument Prune(
        ProjectOverviewDocument document,
        DateTimeOffset now,
        ISet<string>? activeThreadIds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!ProjectOverviewDocumentValidator.TryValidate(document, out var validationError))
            throw new InvalidDataException(validationError);

        var projectMap = document.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var kept = document.Updates.Where(update =>
        {
            if (!projectMap.TryGetValue(update.ProjectId, out var project)) return false;
            if (project.IsPinned || update.NeedsInput || activeThreadIds?.Contains(update.ThreadId) == true) return true;
            return now - update.OccurredAt < Retention;
        }).ToList();
        var updateRevisions = kept.Select(update => (update.Id, update.Revision)).ToHashSet();
        var expiredProjectIds = document.Projects.Where(project =>
            !project.IsPinned && project.LastMeaningfulActivity is { } activity && now - activity >= Retention &&
            !kept.Any(update => update.ProjectId == project.Id) &&
            !document.Memberships.Any(membership => membership.ProjectId == project.Id && activeThreadIds?.Contains(membership.ThreadId) == true))
            .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var retainedProjectIds = document.Projects.Select(project => project.Id).Where(id => !expiredProjectIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        return document with
        {
            Projects = document.Projects.Where(project => retainedProjectIds.Contains(project.Id)).Select(project =>
                !project.IsPinned && project.LastMeaningfulActivity is { } activity && now - activity >= Retention &&
                !document.Memberships.Any(membership => membership.ProjectId == project.Id && activeThreadIds?.Contains(membership.ThreadId) == true)
                    ? project with { CurrentSummary = null }
                    : project).ToList(),
            Memberships = document.Memberships.Where(item => retainedProjectIds.Contains(item.ProjectId)).ToList(),
            ThreadReferences = document.ThreadReferences.Where(reference =>
                now - reference.LastActivity < Retention || activeThreadIds?.Contains(reference.ThreadId) == true ||
                document.Projects.Any(project => project.IsPinned && document.Memberships.Any(item => item.ProjectId == project.Id && item.ThreadId == reference.ThreadId))).ToList(),
            Updates = kept,
            Acknowledgements = document.Acknowledgements
                .Where(item => updateRevisions.Contains((item.UpdateId, item.Revision)))
                .ToList(),
            Coverage = document.Coverage
                .Where(checkpoint => checkpoint.ProjectId is null || retainedProjectIds.Contains(checkpoint.ProjectId))
                .Where(checkpoint => checkpoint.ProjectId is null
                    ? now - checkpoint.CoveredAt < Retention || activeThreadIds?.Contains(checkpoint.ThreadId) == true
                    : projectMap[checkpoint.ProjectId].IsPinned || now - checkpoint.CoveredAt < Retention || activeThreadIds?.Contains(checkpoint.ThreadId) == true)
                .ToList(),
        };
    }

    /// <summary>Deletes a project and all text, membership, acknowledgements and checkpoints owned by it.</summary>
    public static ProjectOverviewDocument DeleteProject(ProjectOverviewDocument document, string projectId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var removedUpdateIds = document.Updates
            .Where(update => string.Equals(update.ProjectId, projectId, StringComparison.Ordinal))
            .Select(update => update.Id)
            .ToHashSet(StringComparer.Ordinal);
        var memberships = document.Memberships.Where(item => item.ProjectId != projectId).ToList();
        var retainedThreads = memberships.Select(item => item.ThreadId).ToHashSet(StringComparer.Ordinal);
        return document with
        {
            Projects = document.Projects.Where(project => project.Id != projectId).ToList(),
            Memberships = memberships,
            ThreadReferences = document.ThreadReferences.Where(reference => retainedThreads.Contains(reference.ThreadId)).ToList(),
            Updates = document.Updates.Where(update => update.ProjectId != projectId).ToList(),
            Acknowledgements = document.Acknowledgements.Where(item => !removedUpdateIds.Contains(item.UpdateId)).ToList(),
            Coverage = document.Coverage.Where(item => item.ProjectId != projectId).ToList(),
        };
    }

    public static ProjectOverviewDocument Clear() => new();
}

/// <summary>Validates deserialised state before it can reach the view or replace a recoverable snapshot.</summary>
public static class ProjectOverviewDocumentValidator
{
    public const int MaximumProjects = 1000;
    public const int MaximumEntries = 10000;

    public static bool TryValidate(ProjectOverviewDocument? document, out string diagnosticCode)
    {
        diagnosticCode = "StoredDataMalformed";
        if (document is null || document.SchemaVersion != ProjectOverviewDocument.CurrentSchemaVersion ||
            document.Projects is null || document.Memberships is null || document.Updates is null ||
            document.Acknowledgements is null || document.Coverage is null || document.ThreadReferences is null ||
            document.Projects.Count > MaximumProjects || document.Memberships.Count > MaximumEntries ||
            document.Updates.Count > MaximumEntries || document.Acknowledgements.Count > MaximumEntries ||
            document.Coverage.Count > MaximumEntries || document.ThreadReferences.Count > MaximumEntries)
        {
            diagnosticCode = document?.SchemaVersion != ProjectOverviewDocument.CurrentSchemaVersion
                ? "StoredDataSchemaUnsupported"
                : "StoredDataMalformed";
            return false;
        }

        var projects = new Dictionary<string, ProjectDefinition>(StringComparer.Ordinal);
        foreach (var project in document.Projects)
        {
            if (project is null || project.Id is not { Length: > 0 and <= ProjectTextLimits.MaximumEvidenceIdLength } ||
                project.Name is not { Length: > 0 and <= 120 } ||
                project.CurrentSummary is { Length: > 1000 } ||
                !IsSanitized(project.Name, 120) || (project.CurrentSummary is { } summary && !IsSanitized(summary, 1000)) ||
                !projects.TryAdd(project.Id, project)) return false;
        }

        var memberships = new HashSet<(string ProjectId, string ThreadId)>();
        foreach (var membership in document.Memberships)
        {
            if (membership is null || membership.ProjectId is null || membership.ProjectId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
                membership.ThreadId is not { Length: > 0 and <= ProjectTextLimits.MaximumEvidenceIdLength } ||
                !Enum.IsDefined(membership.Source) ||
                (membership.IsUnassigned
                    ? membership.Source != ProjectMembershipSource.Manual || membership.ProjectId.Length != 0
                    : membership.ProjectId.Length == 0 || !projects.ContainsKey(membership.ProjectId)) ||
                !memberships.Add((membership.ProjectId, membership.ThreadId))) return false;
        }

        var threadReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in document.ThreadReferences)
        {
            if (reference is null || reference.ThreadId is not { Length: > 0 and <= ProjectTextLimits.MaximumEvidenceIdLength } ||
                !Enum.IsDefined(reference.Provider) || reference.DisplayTitle is not { Length: > 0 and <= 120 } ||
                !IsSanitized(reference.DisplayTitle, 120) || !threadReferences.Add(reference.ThreadId)) return false;
        }

        var updateIds = new HashSet<string>(StringComparer.Ordinal);
        var revisions = new HashSet<(string Id, int Revision)>();
        foreach (var update in document.Updates)
        {
            if (update is null || !ProjectTextLimits.IsBounded(update) || !projects.ContainsKey(update.ProjectId) ||
                !IsSanitized(update.LatestAction, ProjectTextLimits.MaximumActionLength) ||
                update.Progress.Any(line => !IsSanitized(line, ProjectTextLimits.MaximumProgressLength)) ||
                !memberships.Contains((update.ProjectId, update.ThreadId)) || !updateIds.Add(update.Id)) return false;
            revisions.Add((update.Id, update.Revision));
        }

        var acknowledgements = new HashSet<(string Id, int Revision)>();
        foreach (var item in document.Acknowledgements)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.UpdateId) || item.UpdateId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
                item.Revision <= 0 || !revisions.Contains((item.UpdateId, item.Revision)) ||
                !acknowledgements.Add((item.UpdateId, item.Revision))) return false;
        }

        var checkpoints = new HashSet<(string? ProjectId, string ThreadId, string TurnId)>();
        foreach (var item in document.Coverage)
        {
            if (item is null || (item.ProjectId is not null && string.IsNullOrWhiteSpace(item.ProjectId)) || string.IsNullOrWhiteSpace(item.ThreadId) ||
                string.IsNullOrWhiteSpace(item.TurnId) || item.TurnId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
                string.IsNullOrWhiteSpace(item.SchemaVersion) || item.SchemaVersion.Length > 32 ||
                string.IsNullOrWhiteSpace(item.PromptVersion) || item.PromptVersion.Length > 32 ||
                string.IsNullOrWhiteSpace(item.Model) || item.Model.Length > 64 ||
                (item.ProjectId is { } projectId && !memberships.Contains((projectId, item.ThreadId))) ||
                !checkpoints.Add((item.ProjectId, item.ThreadId, item.TurnId))) return false;
        }

        diagnosticCode = string.Empty;
        return true;
    }

    static bool IsSanitized(string text, int maximumLength) =>
        string.Equals(ProjectSummaryTextSanitizer.Sanitize(text, maximumLength), text, StringComparison.Ordinal);
}
