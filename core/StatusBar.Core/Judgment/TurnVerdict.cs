namespace StatusBar.Core.Judgment;

/// <summary>
/// The AI's answer for one finished turn, kept with that turn's state. A null judgment means the
/// check failed, was skipped or is set to log only, so the built-in rules decide.
/// </summary>
public sealed record TurnVerdict(string EvidenceKey, TurnEndJudgment? Judgment);

/// <summary>How a finished turn's state follows the AI's answer, with the built-in rules as the fallback.</summary>
public static class TurnVerdictPolicy
{
    /// <summary>How long a turn with a captured final message waits for the AI before the rules take over.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How recent a turn that was already finished when the app started must be to still be judged (D26). Older
    /// ones barely matter and would cost AI calls for nothing.
    /// </summary>
    public static readonly TimeSpan StartupJudgmentWindow = TimeSpan.FromMinutes(30);

    /// <summary>Most already-finished turns judged per provider at start-up; the rest use the built-in rules.</summary>
    public const int MaximumStartupJudgments = 10;

    /// <summary>Probability at or above which "the assistant is stopped waiting for the user" counts as yes.</summary>
    public const double AskThreshold = 0.5;

    /// <summary>Probability at or above which a hint about next steps or an offer is shown.</summary>
    public const double HintThreshold = 0.6;

    /// <summary>Row detail for a finished report that lists next steps.</summary>
    public const string NextStepsDetail = "Next steps suggested";

    /// <summary>Row detail for finished work that offers more.</summary>
    public const string FollowUpDetail = "Follow-up offered";

    /// <summary>Probability at or above which a waiting assistant counts as blocked mid-task (urgent) rather than offering a further step.</summary>
    public const double UrgentThreshold = 0.5;

    /// <summary>
    /// Probability at or above which the second-opinion "cannot go on until the user replies" answer agrees the assistant is
    /// blocked (D28). A finished deliverable that notes an optional follow-up scored high on alert and urgent yet low here.
    /// </summary>
    public const double BlockedThreshold = 0.5;

    /// <summary>Row detail for finished work where the assistant only asks whether to take a further step: Steve's call, not urgent.</summary>
    public const string GoAheadDetail = "Ready for your go-ahead";

    /// <summary>Alert wording when the AI reads the assistant as waiting for an answer, or as not waiting.</summary>
    public const string RulesQuestionReason = "Asked you a question";

    /// <summary>Alert wording for a stopped assistant that proposed something.</summary>
    public const string ApprovalReason = "Waiting for your approval";

    /// <summary>Alert wording for a stopped assistant that hit something only the user can fix.</summary>
    public const string StuckReason = "Needs your help";

    /// <summary>Alert wording for a stopped assistant that needs an answer.</summary>
    public const string AnswerReason = "Waiting for your answer";

    /// <summary>
    /// Whether the finished turn is blocked waiting for the user: true or false once decided, or null while the
    /// AI's answer is still awaited (the caller then shows the turn quietly, so a false alert never fires).
    /// </summary>
    public static bool? AsksUser(bool rulesSayQuestion, DateTimeOffset? finalMessageAt, TurnVerdict? verdict, DateTimeOffset now, DateTimeOffset? judgmentRequestedAt = null)
    {
        // No captured text: the AI check is off, or this turn was already over when the app started.
        if (finalMessageAt is not DateTimeOffset at) return rulesSayQuestion;
        if (verdict is not null && Matches(verdict, at))
            return verdict.Judgment is { } judgment ? IsUrgentAlert(judgment) : rulesSayQuestion;
        // A turn recovered at start-up finished long ago, so the wait is measured from when its answer was asked for.
        var waitingSince = judgmentRequestedAt is { } requested && requested > at ? requested : at;
        return now - waitingSince < Grace ? null : rulesSayQuestion;
    }

    /// <summary>
    /// The one rule for the urgent alert: waiting, held up mid-task and confirmed blocked by the second opinion. Any
    /// waiting answer that fails the last two is a calm go-ahead instead. Used by the state and by the monitor log.
    /// </summary>
    public static bool IsUrgentAlert(TurnEndJudgment judgment) =>
        judgment.Alert >= AskThreshold && judgment.Urgent >= UrgentThreshold && judgment.Blocked >= BlockedThreshold;

    /// <summary>
    /// The turns to judge among those found already finished at start-up: recent enough, newest first, capped so a
    /// restart with many sessions cannot flood the AI. Returns indexes into <paramref name="finalMessageTimes"/>.
    /// </summary>
    public static IReadOnlyList<int> SelectStartupJudgments(IReadOnlyList<DateTimeOffset> finalMessageTimes, DateTimeOffset now) =>
        Enumerable.Range(0, finalMessageTimes.Count)
            .Where(i => now - finalMessageTimes[i] < StartupJudgmentWindow)
            .OrderByDescending(i => finalMessageTimes[i])
            .Take(MaximumStartupJudgments)
            .ToArray();

    /// <summary>The alert wording for a turn the AI reads as stopped; the rules' wording when it has not decided.</summary>
    public static string AttentionReason(DateTimeOffset? finalMessageAt, TurnVerdict? verdict)
    {
        if (finalMessageAt is DateTimeOffset at && verdict is { Judgment: { } judgment } && Matches(verdict, at))
        {
            return judgment.AlertKind switch
            {
                TurnEndKind.WaitingForApproval => ApprovalReason,
                TurnEndKind.Stuck => StuckReason,
                _ => AnswerReason,
            };
        }

        return RulesQuestionReason;
    }

    /// <summary>Whether an attention reason means the assistant is waiting on a question or approval (so a Stop hook must not clear it).</summary>
    public static bool IsQuestionReason(string? reason) =>
        reason is RulesQuestionReason or ApprovalReason or StuckReason or AnswerReason;

    /// <summary>A short label for a finished turn that is not urgent (including the calm go-ahead case), or null.</summary>
    public static string? Detail(DateTimeOffset? finalMessageAt, TurnVerdict? verdict)
    {
        if (finalMessageAt is not DateTimeOffset at || verdict is null || !Matches(verdict, at)) return null;
        if (verdict.Judgment is not { } judgment) return null;
        // Waiting but not blocked: the work is done and the assistant asks about a further step.
        if (judgment.Alert >= AskThreshold) return IsUrgentAlert(judgment) ? null : GoAheadDetail;
        if (judgment.Report >= HintThreshold) return NextStepsDetail;
        return judgment.Offer >= HintThreshold ? FollowUpDetail : null;
    }

    /// <summary>The evidence key a provider gives a finished turn's final message.</summary>
    public static string EvidenceKeyFor(DateTimeOffset finalMessageAt) =>
        finalMessageAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static bool Matches(TurnVerdict verdict, DateTimeOffset at) =>
        string.Equals(verdict.EvidenceKey, EvidenceKeyFor(at), StringComparison.Ordinal);
}
