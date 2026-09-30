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

    /// <summary>Probability at or above which the AI's "asks the user" answer counts as yes.</summary>
    public const double AskThreshold = 0.5;

    /// <summary>Probability at or above which a "needs review" answer is shown.</summary>
    public const double ReviewThreshold = 0.6;

    /// <summary>Probability at or above which a "follow-on work offered" answer is shown.</summary>
    public const double FollowUpThreshold = 0.6;

    /// <summary>Row detail for a finished turn the AI thinks needs a look.</summary>
    public const string ReviewDetail = "Review suggested";

    /// <summary>Row detail for a finished turn that offers more work.</summary>
    public const string FollowUpDetail = "Follow-up offered";

    /// <summary>
    /// Whether the finished turn asked the user something: true or false once decided, or null while the
    /// AI's answer is still awaited (the caller then shows the turn quietly, so a false alert never fires).
    /// </summary>
    public static bool? AsksUser(bool rulesSayQuestion, DateTimeOffset? finalMessageAt, TurnVerdict? verdict, DateTimeOffset now)
    {
        // No captured text: the AI check is off, or this turn was already over when the app started.
        if (finalMessageAt is not DateTimeOffset at) return rulesSayQuestion;
        if (verdict is not null && Matches(verdict, at))
            return verdict.Judgment is { } judgment ? judgment.AsksUser >= AskThreshold : rulesSayQuestion;
        return now - at < Grace ? null : rulesSayQuestion;
    }

    /// <summary>A short label for a finished, non-question turn, or null.</summary>
    public static string? Detail(DateTimeOffset? finalMessageAt, TurnVerdict? verdict)
    {
        if (finalMessageAt is not DateTimeOffset at || verdict is null || !Matches(verdict, at)) return null;
        if (verdict.Judgment is not { } judgment) return null;
        if (judgment.NeedsReview >= ReviewThreshold) return ReviewDetail;
        return judgment.OffersFollowUp >= FollowUpThreshold ? FollowUpDetail : null;
    }

    /// <summary>The evidence key a provider gives a finished turn's final message.</summary>
    public static string EvidenceKeyFor(DateTimeOffset finalMessageAt) =>
        finalMessageAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static bool Matches(TurnVerdict verdict, DateTimeOffset at) =>
        string.Equals(verdict.EvidenceKey, EvidenceKeyFor(at), StringComparison.Ordinal);
}
