using StatusBar.Core.Tasks;

namespace StatusBar.Core.Judgment;

/// <summary>A finished turn seen live, with what the built-in rules decided about it.</summary>
/// <param name="Provider">The provider that owns the task.</param>
/// <param name="TaskKey">Content-free label such as <c>codex:1a2b3c4d</c>, for the log.</param>
/// <param name="EvidenceKey">Identifies this finish so one turn is judged once.</param>
/// <param name="Heuristic">What the built-in rules decided: <c>question</c>, <c>card</c> (a structured question) or <c>none</c>.</param>
/// <param name="Text">The tail of the final message. Held in memory for the check only; never logged.</param>
/// <param name="Start">The start of the message when it is longer than the tail; same handling.</param>
public sealed record TurnEndInfo(
    AgentProvider Provider,
    string TaskKey,
    string EvidenceKey,
    string Heuristic,
    string Text,
    string? Start = null);

/// <summary>Receives a finished turn from a provider.</summary>
public delegate void TurnEndHandler(TurnEndInfo info);

/// <summary>How an assistant's final message leaves things. The first three mean it is stopped waiting for the user.</summary>
public enum TurnEndKind
{
    /// <summary>It asked a specific question or needs a decision, and cannot continue without the reply.</summary>
    WaitingForAnswer,

    /// <summary>It proposed a plan or action and is stopped, waiting for permission.</summary>
    WaitingForApproval,

    /// <summary>It hit an error or missing access it cannot resolve and needs the user to fix something.</summary>
    Stuck,

    /// <summary>It chose a way forward and is continuing by itself, only inviting objections.</summary>
    CarryingOn,

    /// <summary>It delivered a review or result and lists next steps or inputs that would help later; nothing is blocked.</summary>
    ReportWithNextSteps,

    /// <summary>The work is done and it offers optional extra work.</summary>
    FinishedWithOffer,

    /// <summary>The work is done with nothing further proposed.</summary>
    Finished,
}

/// <summary>
/// The AI's reading of a final message. <c>Alert</c> (the probability that the assistant is stopped waiting
/// for the user, by any of the first three kinds) is what raises an alert; <c>Blocked</c> and <c>Asks</c>
/// are two other wordings of the same question, kept only so the log can compare them. <c>Urgent</c> separates
/// an assistant blocked mid-task from one that finished and only asks about a further step; it is consulted
/// only once <c>Alert</c> has fired.
/// </summary>
/// <param name="Kind">The most likely kind of ending.</param>
/// <param name="AlertKind">The most likely of the three waiting kinds, used for the alert's wording.</param>
/// <param name="Alert">Probability that the assistant is stopped waiting for the user.</param>
/// <param name="Report">Probability that the message is a report that lists next steps.</param>
/// <param name="Offer">Probability that the work is done and more is offered.</param>
/// <param name="Blocked">Second opinion: probability the assistant cannot go on until the user replies.</param>
/// <param name="Asks">First wording: probability the message asks for input at all. Over-alerts; logged only.</param>
/// <param name="Urgent">Probability the requested work is unfinished and held up until the user replies (as opposed to done, with a further step offered).</param>
public sealed record TurnEndJudgment(
    TurnEndKind Kind,
    TurnEndKind AlertKind,
    double Alert,
    double Report,
    double Offer,
    double Blocked,
    double Asks,
    double Urgent,
    int InputTokens,
    int OutputTokens);

/// <summary>Judges the final message of a finished turn.</summary>
public interface ITurnEndClassifier
{
    /// <summary>Returns the judgment or throws; callers fall back to the built-in rules.</summary>
    Task<TurnEndJudgment> ClassifyAsync(string finalMessage, CancellationToken cancellationToken, string? messageStart = null);
}
