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
/// Asks TypeSafe's Jev model four yes/no questions about a final message in one request.
/// One message can be both "finished" and "offers follow-on work", so each is its own Noul.
/// </summary>
public sealed class JevTurnEndClassifier : ITurnEndClassifier
{
    /// <summary>The documented endpoint: https://docs.typesafe.ai/api.md.</summary>
    public static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");

    const string Model = "jev-latest";
    const int MaximumAttempts = 2;

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
    public async Task<TurnEndJudgment> ClassifyAsync(string finalMessage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalMessage);
        var body = BuildRequest(finalMessage).ToJsonString();

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

    internal static JsonObject BuildRequest(string finalMessage) => new()
    {
        ["model"] = Model,
        ["state"] = new JsonObject { ["assistant_final_message"] = finalMessage },
        ["questions"] = new JsonObject
        {
            ["asks_user"] = Noul(
                "The state holds the final message an AI coding assistant sent to the user when it stopped working. " +
                "Does the message ask the user a question, or ask for a decision, choice, confirmation, permission or " +
                "information that the assistant needs before it can continue? Rhetorical questions, and questions the " +
                "message itself answers, do not count.",
                "The message ends by waiting for the user's answer, decision or approval.",
                "The message reports results or status and does not wait for an answer."),
            ["needs_review"] = Noul(
                "Has the assistant produced something the user should look over before relying on it, such as a draft, " +
                "a proposed plan, a set of changes to check, or results the assistant itself is unsure about?",
                "The user is expected to inspect or approve the result.",
                "The message is a routine report that needs no inspection."),
            ["offers_follow_up"] = Noul(
                "Does the message suggest or offer further work, next steps or optional improvements the user could ask for?",
                "It proposes or offers something more to do.",
                "It does not propose anything further."),
            ["finished"] = Noul(
                "Does the message report that the requested work is complete, with nothing left unfinished or blocked?",
                "The work is reported as done.",
                "The work is partial, blocked, failed or still in progress."),
        },
    };

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
            double Noul(string name)
            {
                var value = answers[name]?["noul"]?.GetValue<double>() ?? throw new JsonException();
                return Math.Clamp(value, 0, 1);
            }

            var usage = root["usage"]?.AsObject();
            return new TurnEndJudgment(
                Noul("asks_user"),
                Noul("needs_review"),
                Noul("offers_follow_up"),
                Noul("finished"),
                usage?["input_tokens"]?.GetValue<int>() ?? 0,
                usage?["output_tokens"]?.GetValue<int>() ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new TurnEndClassifierException("BadResponse");
        }
    }
}
