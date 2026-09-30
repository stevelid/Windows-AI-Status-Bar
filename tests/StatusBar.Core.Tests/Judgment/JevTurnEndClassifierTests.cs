using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using StatusBar.Core.Judgment;

namespace StatusBar.Core.Tests.Judgment;

public sealed class JevTurnEndClassifierTests
{
    const string SampleResponse = """
        {"model":"jev-1.13.0","answers":{
          "kind":{"type":"choice","choice":"waiting_for_answer",
                  "probabilities":{"waiting_for_answer":0.90,"waiting_for_approval":0.05,"stuck":0.02,
                                   "carrying_on":0.01,"report_with_next_steps":0.01,"finished_with_offer":0.005,"finished":0.005},
                  "confidence":0.8},
          "blocked_on_user":{"type":"noul","noul":0.91},
          "asks_user":{"type":"noul","noul":0.97}},
         "usage":{"input_tokens":304,"output_tokens":18}}
        """;

    [Fact]
    public async Task Sends_one_request_with_the_kind_choice_and_two_noul_second_opinions()
    {
        var handler = new ScriptedHandler(_ => Json(SampleResponse));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "  sample-key  ");

        var judgment = await classifier.ClassifyAsync("Placeholder final message.", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", request.Uri);
        Assert.Equal("Bearer sample-key", request.Authorization);

        var body = JsonNode.Parse(request.Body)!.AsObject();
        Assert.Equal("jev-latest", (string?)body["model"]);
        Assert.Equal("Placeholder final message.", (string?)body["state"]!["assistant_final_message"]);
        Assert.Null(body["state"]!["assistant_message_start"]);
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["kind", "blocked_on_user", "asks_user"], questions.Select(pair => pair.Key).ToArray());
        Assert.Equal("choice", (string?)questions["kind"]!["type"]);
        Assert.Equal(
            ["waiting_for_answer", "waiting_for_approval", "stuck", "carrying_on", "report_with_next_steps", "finished_with_offer", "finished"],
            questions["kind"]!["criteria"]!.AsObject().Select(pair => pair.Key).ToArray());
        foreach (var name in new[] { "blocked_on_user", "asks_user" })
        {
            Assert.Equal("noul", (string?)questions[name]!["type"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)questions[name]!["instructions"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)questions[name]!["criteria"]!["true"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)questions[name]!["criteria"]!["false"]));
        }

        Assert.Equal(TurnEndKind.WaitingForAnswer, judgment.Kind);
        Assert.Equal(TurnEndKind.WaitingForAnswer, judgment.AlertKind);
        Assert.Equal(0.97, judgment.Alert, precision: 6);
        Assert.Equal(0.01, judgment.Report, precision: 6);
        Assert.Equal(0.005, judgment.Offer, precision: 6);
        Assert.Equal(0.91, judgment.Blocked);
        Assert.Equal(0.97, judgment.Asks);
        Assert.Equal(304, judgment.InputTokens);
        Assert.Equal(18, judgment.OutputTokens);
    }

    [Fact]
    public async Task The_wording_separates_a_stopped_assistant_from_a_report_with_next_steps_and_from_one_carrying_on()
    {
        var handler = new ScriptedHandler(_ => Json(SampleResponse));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key");

        await classifier.ClassifyAsync("End of a long report.", CancellationToken.None, "Start of the report.");

        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body)!.AsObject();
        Assert.Equal("Start of the report.", (string?)body["state"]!["assistant_message_start"]);
        Assert.Equal("End of a long report.", (string?)body["state"]!["assistant_final_message"]);
        var kind = body["questions"]!["kind"]!;
        Assert.Contains("actually stopped waiting for the user", (string)kind["instructions"]!, StringComparison.Ordinal);
        Assert.Contains("continuing by itself", (string)kind["criteria"]!["carrying_on"]!, StringComparison.Ordinal);
        Assert.Contains("nothing is blocked", (string)kind["criteria"]!["report_with_next_steps"]!, StringComparison.Ordinal);
        var blocked = (string)body["questions"]!["blocked_on_user"]!["instructions"]!;
        Assert.Contains("Do NOT count", blocked, StringComparison.Ordinal);
        Assert.Contains("optional next steps", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alert_adds_up_the_three_waiting_kinds_and_names_the_most_likely_of_them()
    {
        const string response = """
            {"answers":{"kind":{"type":"choice","choice":"report_with_next_steps",
               "probabilities":{"waiting_for_answer":0.20,"waiting_for_approval":0.30,"stuck":0.05,"carrying_on":0.0,
                                "report_with_next_steps":0.40,"finished_with_offer":0.05,"finished":0.0}},
             "blocked_on_user":{"type":"noul","noul":0.4},"asks_user":{"type":"noul","noul":0.9}}}
            """;
        var classifier = new JevTurnEndClassifier(new HttpClient(new ScriptedHandler(_ => Json(response))), "key");

        var judgment = await classifier.ClassifyAsync("Text", CancellationToken.None);

        Assert.Equal(TurnEndKind.ReportWithNextSteps, judgment.Kind);
        Assert.Equal(TurnEndKind.WaitingForApproval, judgment.AlertKind);
        Assert.Equal(0.55, judgment.Alert, precision: 6);
        Assert.Equal(0.40, judgment.Report, precision: 6);
    }

    [Fact]
    public async Task Rate_limits_are_retried_once_and_then_reported()
    {
        var calls = 0;
        var handler = new ScriptedHandler(_ => ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)529) : Json(SampleResponse));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key", TimeSpan.Zero);

        Assert.Equal(0.97, (await classifier.ClassifyAsync("Text", CancellationToken.None)).Asks);
        Assert.Equal(2, calls);

        var always = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var failing = new JevTurnEndClassifier(new HttpClient(always), "key", TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<TurnEndClassifierException>(() => failing.ClassifyAsync("Text", CancellationToken.None));
        Assert.Equal("Http429", error.Code);
        Assert.Equal(2, always.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Http401")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Http422")]
    public async Task Other_errors_are_reported_without_retrying(HttpStatusCode status, string code)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key", TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<TurnEndClassifierException>(() => classifier.ClassifyAsync("Text", CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"answers\":{}}")]
    [InlineData("{\"answers\":{\"kind\":{\"type\":\"choice\",\"choice\":\"finished\"},\"blocked_on_user\":{\"noul\":0},\"asks_user\":{\"noul\":0}}}")]
    [InlineData("{\"answers\":{\"kind\":{\"probabilities\":{\"finished\":1}},\"blocked_on_user\":{\"noul\":\"high\"},\"asks_user\":{\"noul\":0}}}")]
    public async Task A_response_without_the_expected_answers_is_a_bad_response(string json)
    {
        var handler = new ScriptedHandler(_ => Json(json));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key", TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<TurnEndClassifierException>(() => classifier.ClassifyAsync("Text", CancellationToken.None));

        Assert.Equal("BadResponse", error.Code);
    }

    [Fact]
    public async Task A_client_timeout_and_a_network_failure_become_content_free_codes()
    {
        var timeout = new JevTurnEndClassifier(
            new HttpClient(new ScriptedHandler(_ => throw new TaskCanceledException())), "key", TimeSpan.Zero);
        Assert.Equal("Timeout", (await Assert.ThrowsAsync<TurnEndClassifierException>(
            () => timeout.ClassifyAsync("Text", CancellationToken.None))).Code);

        var network = new JevTurnEndClassifier(
            new HttpClient(new ScriptedHandler(_ => throw new HttpRequestException("secret detail"))), "key", TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<TurnEndClassifierException>(() => network.ClassifyAsync("Text", CancellationToken.None));
        Assert.Equal("Network", error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probabilities_outside_zero_to_one_are_clamped()
    {
        var handler = new ScriptedHandler(_ => Json(SampleResponse.Replace("0.97", "1.4").Replace("0.90", "1.7")));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key");

        var judgment = await classifier.ClassifyAsync("Text", CancellationToken.None);

        Assert.Equal(1, judgment.Asks);
        Assert.Equal(1, judgment.Alert);
    }

    static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    sealed record Seen(HttpMethod Method, string Uri, string? Authorization, string Body);

    sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
