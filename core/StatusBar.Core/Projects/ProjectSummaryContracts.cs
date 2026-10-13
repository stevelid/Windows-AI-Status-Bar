namespace StatusBar.Core.Projects;

/// <summary>A bounded request containing only visible user and final assistant text for one turn.</summary>
public sealed record ProjectSummaryRequest(
    string ThreadId,
    string EvidenceId,
    string DisplayTitle,
    string? UserRequest,
    string AssistantFinal,
    IReadOnlyList<string> CandidateProjectNames,
    string? PriorSummary = null,
    bool PartialCoverage = true,
    DateTimeOffset OccurredAt = default,
    IReadOnlyList<ProjectSummaryEvidence>? EarlierEvidence = null);

/// <summary>One earlier completed turn coalesced into a bounded project summary request.</summary>
public sealed record ProjectSummaryEvidence(string EvidenceId, string? UserRequest, string AssistantFinal, DateTimeOffset OccurredAt);

/// <summary>Validated model output; navigation and status are resolved from trusted local state.</summary>
public sealed record ProjectSummaryResult(
    string? SuggestedProjectName,
    string LatestAction,
    IReadOnlyList<string> Progress,
    IReadOnlyList<string> SourceEvidenceIds);

/// <summary>Replaceable backend for subscription-backed project summaries.</summary>
public interface IProjectSummaryProvider
{
    Task<ProjectSummaryResult?> SummarizeAsync(ProjectSummaryRequest request, CancellationToken cancellationToken);
}

/// <summary>Exact source identity used to exclude app-owned summary runs from ordinary Codex state.</summary>
public static class ProjectSummaryWorkerIdentity
{
    public const string ThreadSource = "windows_ai_status_bar_summary";
}

/// <summary>Strictly validates worker output against the supplied source identifiers and UI bounds.</summary>
public static class ProjectSummaryValidator
{
    public const int MaximumCandidateProjectNames = 100;

    public static bool TryValidate(ProjectSummaryRequest? request, ProjectSummaryResult? result)
    {
        if (!IsValidRequest(request) || result is null) return false;

        if (result.LatestAction is null || result.LatestAction.Length > ProjectTextLimits.MaximumActionLength ||
            result.Progress is null || result.Progress.Count > ProjectTextLimits.MaximumProgressItems ||
            result.Progress.Any(line => string.IsNullOrWhiteSpace(line) || line.Length > ProjectTextLimits.MaximumProgressLength) ||
            result.SuggestedProjectName is { Length: > 120 } ||
            result.SourceEvidenceIds is null || result.SourceEvidenceIds.Count > ProjectTextLimits.MaximumEvidenceIds)
            return false;

        var meaningful = !string.IsNullOrWhiteSpace(result.LatestAction) || result.Progress.Count > 0 ||
                         !string.IsNullOrWhiteSpace(result.SuggestedProjectName);
        if (!meaningful) return result.SourceEvidenceIds.Count == 0;
        if (result.SourceEvidenceIds.Count == 0) return false;

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        AddEvidence(request!.EvidenceId, request.UserRequest, allowed);
        foreach (var prior in request.EarlierEvidence ?? []) AddEvidence(prior.EvidenceId, prior.UserRequest, allowed);
        return result.SourceEvidenceIds.All(allowed.Contains) &&
               result.SourceEvidenceIds.Distinct(StringComparer.Ordinal).Count() == result.SourceEvidenceIds.Count;
    }

    public static bool IsValidRequest(ProjectSummaryRequest? request, bool enforceInputBudget = true)
    {
        if (request is null || !(request.ThreadId?.StartsWith("codex:", StringComparison.Ordinal) == true ||
                request.ThreadId?.StartsWith("claude:", StringComparison.Ordinal) == true) ||
            request.ThreadId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
            string.IsNullOrWhiteSpace(request.EvidenceId) || request.EvidenceId.Length > ProjectTextLimits.MaximumEvidenceIdLength ||
            request.DisplayTitle is null || request.DisplayTitle.Length > 120 ||
            request.AssistantFinal is null || request.AssistantFinal.Length > 1800 ||
            request.UserRequest is { Length: > 1200 } || request.CandidateProjectNames is null || request.CandidateProjectNames.Count > 100)
            return false;
        if (!IsSanitized(request.DisplayTitle, 120) || !IsSanitized(request.AssistantFinal, 1800) ||
            (request.UserRequest is { } user && !IsSanitized(user, 1200)) ||
            (request.PriorSummary is { } priorSummary && !IsSanitized(priorSummary, 1000)) ||
            request.CandidateProjectNames.Any(name => name is null || name.Length > 120 || !IsSanitized(name, 120))) return false;
        if (request.PriorSummary is { Length: > 1000 } || request.EarlierEvidence is { Count: > 7 } ||
            (request.EarlierEvidence ?? []).Any(item => item is null || string.IsNullOrWhiteSpace(item.EvidenceId) ||
                item.EvidenceId.Length > ProjectTextLimits.MaximumEvidenceIdLength || item.AssistantFinal is null || item.AssistantFinal.Length > 1800 ||
                item.UserRequest is { Length: > 1200 } || !IsSanitized(item.AssistantFinal, 1800) ||
                (item.UserRequest is { } earlierUser && !IsSanitized(earlierUser, 1200)))) return false;
        return !enforceInputBudget || IsWithinInputBudget(request);
    }

    public static bool IsWithinInputBudget(ProjectSummaryRequest request)
    {
        static int Bytes(string? value) => value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
        var bytes = 700 + Bytes(request.DisplayTitle) + Bytes(request.PriorSummary) +
            request.CandidateProjectNames.Sum(name => Bytes(name) + 8) +
            Bytes(request.UserRequest) + Bytes(request.AssistantFinal) + Bytes(request.EvidenceId) * 2 + 64;
        foreach (var item in request.EarlierEvidence ?? [])
            bytes += Bytes(item.UserRequest) + Bytes(item.AssistantFinal) + Bytes(item.EvidenceId) * 2 + 64;
        return bytes <= 10_000;
    }

    static bool IsSanitized(string text, int maximumLength) =>
        string.Equals(ProjectSummaryTextSanitizer.Sanitize(text, maximumLength), text, StringComparison.Ordinal);

    static void AddEvidence(string id, string? userRequest, ISet<string> allowed)
    {
        allowed.Add(id + ":assistant");
        if (!string.IsNullOrWhiteSpace(userRequest)) allowed.Add(id + ":user");
    }
}
