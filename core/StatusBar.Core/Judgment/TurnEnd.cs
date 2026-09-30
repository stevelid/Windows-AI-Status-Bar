using StatusBar.Core.Tasks;

namespace StatusBar.Core.Judgment;

/// <summary>A finished turn seen live, with what the built-in rules decided about it.</summary>
/// <param name="Provider">The provider that owns the task.</param>
/// <param name="TaskKey">Content-free label such as <c>codex:1a2b3c4d</c>, for the log.</param>
/// <param name="EvidenceKey">Identifies this finish so one turn is judged once.</param>
/// <param name="Heuristic">What the built-in rules decided: <c>question</c>, <c>card</c> (a structured question) or <c>none</c>.</param>
/// <param name="Text">The tail of the final message. Held in memory for the check only; never logged.</param>
public sealed record TurnEndInfo(
    AgentProvider Provider,
    string TaskKey,
    string EvidenceKey,
    string Heuristic,
    string Text);

/// <summary>Receives a finished turn from a provider.</summary>
public delegate void TurnEndHandler(TurnEndInfo info);

/// <summary>Probabilities (0–1) that each statement holds for a final message.</summary>
public sealed record TurnEndJudgment(
    double AsksUser,
    double NeedsReview,
    double OffersFollowUp,
    double Finished,
    int InputTokens,
    int OutputTokens);

/// <summary>Judges the final message of a finished turn.</summary>
public interface ITurnEndClassifier
{
    /// <summary>Returns the judgment or throws; callers fall back to the built-in rules.</summary>
    Task<TurnEndJudgment> ClassifyAsync(string finalMessage, CancellationToken cancellationToken);
}
