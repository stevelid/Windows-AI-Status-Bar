using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StatusBar.Core.Judgment;

/// <summary>A failed call to the classifier, reduced to a content-free code for the log.</summary>
public sealed class TurnEndClassifierException(string code, HttpStatusCode? status = null)
    : Exception(code)
{
    /// <summary>Short code, e.g. <c>Http401</c>, <c>Timeout</c>, <c>BadResponse</c>.</summary>
    public string Code { get; } = code;

    /// <summary>The HTTP status when there was one.</summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Asks TypeSafe's Jev model, in one request, what kind of ending a final message is (which drives the alert)
/// a second choice that splits a blocked assistant from a finished one asking about a further step, and two
/// yes/no questions that are kept as second opinions for the log. The wording was chosen by trying
/// alternatives on made-up messages of the shapes seen in real use; see docs/PROGRESS.md (D22).
/// </summary>
public sealed class JevTurnEndClassifier : ITurnEndClassifier
{
    /// <summary>The documented endpoint: https://docs.typesafe.ai/api.md.</summary>
    public static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");

    /// <summary>Which wording of the questions is in use; logged with each decision so results can be compared across changes.</summary>
    public const int PromptVersion = 4;

    const string Model = "jev-latest";
    const int MaximumAttempts = 2;

    // Option ids of the "kind" question, in the order of TurnEndKind.
    static readonly (string Id, TurnEndKind Kind)[] KindOptions =
    [
        ("waiting_for_answer", TurnEndKind.WaitingForAnswer),
        ("waiting_for_approval", TurnEndKind.WaitingForApproval),
        ("stuck", TurnEndKind.Stuck),
        ("carrying_on", TurnEndKind.CarryingOn),
        ("report_with_next_steps", TurnEndKind.ReportWithNextSteps),
        ("finished_with_offer", TurnEndKind.FinishedWithOffer),
        ("finished", TurnEndKind.Finished),
    ];

    readonly HttpClient _http;
    readonly string _apiKey;
    readonly TimeSpan _retryDelay;

    /// <summary>Creates a classifier. The client's timeout applies to each attempt.</summary>
    public JevTurnEndClassifier(HttpClient http, string apiKey, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _http = http;
        _apiKey = apiKey.Trim();
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc />
    public async Task<TurnEndJudgment> ClassifyAsync(string finalMessage, CancellationToken cancellationToken, string? messageStart = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalMessage);
        var body = BuildRequest(finalMessage, messageStart).ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // The API documents 429 and 529 as "back off and retry".
                if (response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)529 && attempt < MaximumAttempts)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new TurnEndClassifierException("Http" + (int)response.StatusCode, response.StatusCode);

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseResponse(json);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The HttpClient timeout fired, not the caller.
                if (attempt < MaximumAttempts)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new TurnEndClassifierException("Timeout");
            }
            catch (HttpRequestException)
            {
                if (attempt < MaximumAttempts)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new TurnEndClassifierException("Network");
            }
        }
    }

    internal static JsonObject BuildRequest(string finalMessage, string? messageStart = null) => new()
    {
        ["model"] = Model,
        ["state"] = State(finalMessage, messageStart),
        ["questions"] = new JsonObject
        {
            // Drives the alert. One exclusive choice beat the yes/no wordings on 30 made-up messages: every
            // real blocker scored 0.91–1.00 (summed over the three waiting kinds) and every quiet case at most
            // 0.20. "carrying_on" matters: without it "I'm starting on A now, shout if you'd rather B" was read
            // as waiting for approval.
            ["kind"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] =
                    "The state holds the final message an AI assistant sent when it stopped working. Which kind of ending is it? " +
                    "Decide by whether the assistant is actually stopped waiting for the user, not by whether the message contains " +
                    "a question or mentions next steps. When the message is long, assistant_message_start shows how it began and " +
                    "assistant_final_message is its end.",
                ["criteria"] = new JsonObject
                {
                    ["waiting_for_answer"] = "The assistant asked a specific question or needs a decision, and cannot continue without the reply.",
                    ["waiting_for_approval"] = "The assistant proposes a plan or action and is stopped, waiting for permission to proceed.",
                    ["stuck"] = "The assistant hit an error or missing access it cannot resolve and needs the user to fix something.",
                    ["carrying_on"] = "The assistant chose a way forward and is continuing by itself; it only invites the user to object if they disagree.",
                    ["report_with_next_steps"] = "The assistant delivered a review, findings or a result and lists next steps or inputs that would help later; nothing is blocked.",
                    ["finished_with_offer"] = "The work is done and the assistant offers optional extra work or asks if anything else is needed.",
                    ["finished"] = "The work is done with nothing further proposed.",
                },
            },

            // Second opinion, logged beside the kind so the two can be compared.
            ["blocked_on_user"] = Noul(
                "The state holds the end of the final message an AI coding assistant sent to the user when it stopped working " +
                "(assistant_final_message), and, when the message is long, how it began (assistant_message_start). " +
                "Is the assistant now stopped, unable to make any further progress until the user answers a specific question " +
                "or makes a decision? Count: a question it needs answered before it can go on, a request for permission or " +
                "approval, or a blocker it cannot work around. Do NOT count: a report of results, findings or analysis; a review " +
                "that lists issues; suggestions or recommendations; optional next steps; an offer to do more; or a list of " +
                "information the user could supply later to improve or extend the work. If the message delivers something to read " +
                "and only says what would help next, the answer is no.",
                "Work is paused and only the user's reply lets it continue.",
                "The assistant delivered a result, suggestions or optional next steps; the user can read it when convenient."),

            // The first wording. It also counted "information the assistant needs", so it over-alerted on reports that
            // end with next steps (0.92–0.95 on made-up examples of them); kept for the log only.
            ["asks_user"] = Noul(
                "The state holds the final message an AI coding assistant sent to the user when it stopped working. " +
                "Does the message ask the user a question, or ask for a decision, choice, confirmation, permission or " +
                "information that the assistant needs before it can continue? Rhetorical questions, and questions the " +
                "message itself answers, do not count.",
                "The message ends by waiting for the user's answer, decision or approval.",
                "The message reports results or status and does not wait for an answer."),

            // Consulted only when "kind" has already said the assistant is waiting. It tells a blocker apart from
            // finished work that merely asks about a further step ("shall I merge it?"), which Steve wants as a
            // calm "your call". On its own it re-introduced a false alarm on a report with next steps (0.72), so
            // it is never used alone. See D25 in docs/PROGRESS.md.
            ["urgency"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] =
                    "The state holds the final message an AI assistant sent when it stopped working. Which kind of ending is it? " +
                    "Decide by what happens if the user does not reply for a few hours, not by whether the message contains a question.",
                ["criteria"] = new JsonObject
                {
                    ["blocked_mid_task"] = "The work the user asked for is not finished and cannot be finished until the user answers a question, makes a choice, gives permission or fixes something. Waiting holds the task up.",
                    ["next_step_offer"] = "The work the user asked for is done (or handed over for review). The assistant asks whether to take a further step, such as merging, sending, publishing or doing a follow-on part. Nothing is lost if the user answers later.",
                    ["carrying_on"] = "The assistant chose a way forward and is continuing by itself; it only invites the user to object if they disagree.",
                    ["report_with_next_steps"] = "The assistant delivered a review, findings or a result and lists next steps or inputs that would help later; nothing is blocked.",
                    ["finished"] = "The work is done with nothing further proposed or asked.",
                },
            },
        },
    };

    static JsonObject State(string finalMessage, string? messageStart)
    {
        var state = new JsonObject();
        if (!string.IsNullOrWhiteSpace(messageStart)) state["assistant_message_start"] = messageStart;
        state["assistant_final_message"] = finalMessage;
        return state;
    }

    static JsonObject Noul(string instructions, string yes, string no) => new()
    {
        ["type"] = "noul",
        ["instructions"] = instructions,
        ["criteria"] = new JsonObject { ["true"] = yes, ["false"] = no },
    };

    internal static TurnEndJudgment ParseResponse(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException();
            var answers = root["answers"]?.AsObject() ?? throw new JsonException();

            double Noul(string name) =>
                Math.Clamp(answers[name]?["noul"]?.GetValue<double>() ?? throw new JsonException(), 0, 1);

            var kind = answers["kind"]?.AsObject() ?? throw new JsonException();
            var probabilities = kind["probabilities"]?.AsObject() ?? throw new JsonException();
            double P(string id) => Math.Clamp(probabilities[id]?.GetValue<double>() ?? 0, 0, 1);

            var top = KindOptions.MaxBy(option => P(option.Id));
            var alertKind = KindOptions.Take(3).MaxBy(option => P(option.Id)).Kind;
            var alert = Math.Min(1, P("waiting_for_answer") + P("waiting_for_approval") + P("stuck"));

            var urgency = answers["urgency"]?["probabilities"]?.AsObject() ?? throw new JsonException();
            var urgent = Math.Clamp(urgency["blocked_mid_task"]?.GetValue<double>() ?? 0, 0, 1);

            var usage = root["usage"]?.AsObject();
            return new TurnEndJudgment(
                top.Kind,
                alertKind,
                alert,
                P("report_with_next_steps"),
                P("finished_with_offer"),
                Noul("blocked_on_user"),
                Noul("asks_user"),
                urgent,
                usage?["input_tokens"]?.GetValue<int>() ?? 0,
                usage?["output_tokens"]?.GetValue<int>() ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new TurnEndClassifierException("BadResponse");
        }
    }
}
