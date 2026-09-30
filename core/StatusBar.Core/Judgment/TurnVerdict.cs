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

    /// <summary>Probability at or above which "the assistant is stopped waiting for the user" counts as yes.</summary>
    public const double AskThreshold = 0.5;

    /// <summary>Probability at or above which a hint about next steps or an offer is shown.</summary>
    public const double HintThreshold = 0.6;

    /// <summary>Row detail for a finished report that lists next steps.</summary>
    public const string NextStepsDetail = "Next steps suggested";

    /// <summary>Row detail for finished work that offers more.</summary>
    public const string FollowUpDetail = "Follow-up offered";

    /// <summary>Alert wording when the AI reads the assistant as waiting for an answer, or as not waiting.</summary>
    public const string RulesQuestionReason = "Asked you a question";

    /// <summary>Alert wording for a stopped assistant that proposed something.</summary>
    public const string ApprovalReason = "Waiting for your approval";

    /// <summary>Alert wording for a stopped assistant that hit something only the user can fix.</summary>
    public const string StuckReason = "Needs your help";

    /// <summary>Alert wording for a stopped assistant that needs an answer.</summary>
    public const string AnswerReason = "Waiting for your answer";

    /// <summary>
    /// Whether the finished turn is stopped waiting for the user: true or false once decided, or null while the
    /// AI's answer is still awaited (the caller then shows the turn quietly, so a false alert never fires).
    /// </summary>
    public static bool? AsksUser(bool rulesSayQuestion, DateTimeOffset? finalMessageAt, TurnVerdict? verdict, DateTimeOffset now)
    {
        // No captured text: the AI check is off, or this turn was already over when the app started.
        if (finalMessageAt is not DateTimeOffset at) return rulesSayQuestion;
        if (verdict is not null && Matches(verdict, at))
            return verdict.Judgment is { } judgment ? judgment.Alert >= AskThreshold : rulesSayQuestion;
        return now - at < Grace ? null : rulesSayQuestion;
    }

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

    /// <summary>A short label for a finished, not-waiting turn, or null.</summary>
    public static string? Detail(DateTimeOffset? finalMessageAt, TurnVerdict? verdict)
    {
        if (finalMessageAt is not DateTimeOffset at || verdict is null || !Matches(verdict, at)) return null;
        if (verdict.Judgment is not { } judgment || judgment.Alert >= AskThreshold) return null;
        if (judgment.Report >= HintThreshold) return NextStepsDetail;
        return judgment.Offer >= HintThreshold ? FollowUpDetail : null;
    }

    /// <summary>The evidence key a provider gives a finished turn's final message.</summary>
    public static string EvidenceKeyFor(DateTimeOffset finalMessageAt) =>
        finalMessageAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static bool Matches(TurnVerdict verdict, DateTimeOffset at) =>
        string.Equals(verdict.EvidenceKey, EvidenceKeyFor(at), StringComparison.Ordinal);
}
