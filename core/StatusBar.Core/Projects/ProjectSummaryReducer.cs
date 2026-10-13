namespace StatusBar.Core.Projects;

/// <summary>Applies a validated summary and its coverage checkpoint as one immutable Core transition.</summary>
public static class ProjectSummaryReducer
{
    public static ProjectOverviewDocument Apply(
        ProjectOverviewDocument document,
        ProjectSummaryRequest request,
        ProjectSummaryResult result,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!ProjectSummaryValidator.TryValidate(request, result)) throw new InvalidDataException("SummaryOutputInvalid");
        var incomingTurns = (request.EarlierEvidence ?? []).Select(item => item.EvidenceId).Append(request.EvidenceId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (incomingTurns.All(turnId => document.Coverage.Any(item => item.ThreadId == request.ThreadId && item.TurnId == turnId)))
            return document;

        var memberships = document.Memberships.ToList();
        var existing = memberships.Where(item => item.ThreadId == request.ThreadId).ToArray();
        var manual = existing.Where(item => item.Source == ProjectMembershipSource.Manual).ToArray();
        var explicitReferences = existing.Where(item => item.Source == ProjectMembershipSource.ExplicitReference).ToArray();
        var preferred = manual.Length > 0 ? manual : explicitReferences.Length > 0 ? explicitReferences : [];
        var projects = document.Projects.ToList();
        string? suggestedId = null;
        if (preferred.Length == 0 && !string.IsNullOrWhiteSpace(result.SuggestedProjectName))
        {
            var name = ProjectSummaryTextSanitizer.Sanitize(result.SuggestedProjectName, 120);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var project = projects.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                suggestedId = project?.Id ?? "suggested-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..24].ToLowerInvariant();
                if (project is null) projects.Add(new ProjectDefinition { Id = suggestedId, Name = name });
            }
        }

        var targetIds = preferred.Length > 0 ? preferred.Where(item => !item.IsUnassigned).Select(item => item.ProjectId).Distinct(StringComparer.Ordinal).ToArray()
            : suggestedId is not null ? [suggestedId] : [];
        if (preferred.Length == 0 && suggestedId is not null && !memberships.Any(item => item.ThreadId == request.ThreadId && item.ProjectId == suggestedId))
            memberships.Add(new ProjectMembership(suggestedId, request.ThreadId, ProjectMembershipSource.Suggested));

        var now = timeProvider.GetUtcNow();
        var occurredAt = request.OccurredAt == default ? now : request.OccurredAt;
        var latestAction = ProjectSummaryTextSanitizer.Sanitize(result.LatestAction, ProjectTextLimits.MaximumActionLength) ?? string.Empty;
        var progress = result.Progress.Select(line => ProjectSummaryTextSanitizer.Sanitize(line, ProjectTextLimits.MaximumProgressLength))
            .Where(line => !string.IsNullOrWhiteSpace(line)).Cast<string>().Take(ProjectTextLimits.MaximumProgressItems).ToArray();
        var updates = document.Updates.ToList();
        var acknowledgements = document.Acknowledgements.ToList();
        foreach (var projectId in targetIds)
        {
            if (latestAction.Length == 0 && progress.Length == 0) continue;
            var updateId = "u-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.ThreadId + "\n" + request.EvidenceId + "\n" + projectId)))[..24].ToLowerInvariant();
            var revision = updates.Where(item => item.ProjectId == projectId && item.ThreadId == request.ThreadId)
                .Select(item => item.Revision).DefaultIfEmpty().Max() + 1;
            var sourceIds = result.SourceEvidenceIds.Take(ProjectTextLimits.MaximumEvidenceIds).ToArray();
            var update = new ProjectUpdate
            {
                Id = updateId, ProjectId = projectId, ThreadId = request.ThreadId, Revision = revision,
                OccurredAt = occurredAt, LatestAction = latestAction.Length > 0 ? latestAction : progress[0],
                Progress = progress, SourceEvidenceIds = sourceIds,
            };
            if (!ProjectTextLimits.IsBounded(update)) throw new InvalidDataException("SummaryOutputInvalid");
            if (!updates.Any(item => item.Id == updateId))
            {
                updates.Add(update);
                acknowledgements.RemoveAll(item => item.UpdateId == updateId);
            }
            projects = projects.Select(project => project.Id == projectId
                ? project with { CurrentSummary = latestAction.Length > 0 ? latestAction : project.CurrentSummary, LastMeaningfulActivity = occurredAt }
                : project).ToList();
        }

        var coveredTurns = (request.EarlierEvidence ?? []).Select(item => item.EvidenceId)
            .Append(request.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
        var coverage = document.Coverage.Where(item => item.ThreadId != request.ThreadId || !coveredTurns.Contains(item.TurnId, StringComparer.Ordinal)).ToList();
        foreach (var turnId in coveredTurns)
        {
            var turnTime = turnId == request.EvidenceId ? occurredAt : (request.EarlierEvidence ?? []).First(item => item.EvidenceId == turnId).OccurredAt;
            var projectIds = targetIds.Length > 0 ? targetIds.Cast<string?>() : [null];
            foreach (var projectId in projectIds)
                coverage.Add(new ProjectCoverageCheckpoint(projectId, request.ThreadId, turnId,
                    turnTime == default ? occurredAt : turnTime, "1", "project-overview-v1", "gpt-6-luna"));
        }
        var updated = document with
        {
            Projects = projects, Memberships = memberships, Updates = updates,
            Acknowledgements = acknowledgements, Coverage = coverage,
            ThreadReferences = document.ThreadReferences.Where(item => item.ThreadId != request.ThreadId)
                .Append(new ProjectThreadReference(request.ThreadId,
                    request.ThreadId.StartsWith("claude:", StringComparison.Ordinal) ? StatusBar.Core.Tasks.AgentProvider.Claude : StatusBar.Core.Tasks.AgentProvider.Codex,
                    ProjectSummaryTextSanitizer.Sanitize(request.DisplayTitle, 120) ?? "AI conversation", occurredAt)).ToList(),
        };
        if (!ProjectOverviewDocumentValidator.TryValidate(updated, out var error)) throw new InvalidDataException(error);
        return updated;
    }
}
