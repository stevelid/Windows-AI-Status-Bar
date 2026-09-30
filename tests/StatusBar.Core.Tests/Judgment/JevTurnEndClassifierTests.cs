using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using StatusBar.Core.Judgment;

namespace StatusBar.Core.Tests.Judgment;

public sealed class JevTurnEndClassifierTests
{
    const string SampleResponse = """
        {"model":"jev-1.13.0","answers":{
          "asks_user":{"type":"noul","noul":0.97},
          "needs_review":{"type":"noul","noul":0.12},
          "offers_follow_up":{"type":"noul","noul":0.05},
          "finished":{"type":"noul","noul":0.9}},
         "usage":{"input_tokens":304,"output_tokens":18}}
        """;

    [Fact]
    public async Task Sends_one_request_with_four_noul_questions_and_the_bearer_key()
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
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["asks_user", "needs_review", "offers_follow_up", "finished"], questions.Select(pair => pair.Key).ToArray());
        foreach (var (_, question) in questions)
        {
            Assert.Equal("noul", (string?)question!["type"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)question["instructions"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)question["criteria"]!["true"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)question["criteria"]!["false"]));
        }

        Assert.Equal(0.97, judgment.AsksUser);
        Assert.Equal(0.12, judgment.NeedsReview);
        Assert.Equal(0.05, judgment.OffersFollowUp);
        Assert.Equal(0.9, judgment.Finished);
        Assert.Equal(304, judgment.InputTokens);
        Assert.Equal(18, judgment.OutputTokens);
    }

    [Fact]
    public async Task Rate_limits_are_retried_once_and_then_reported()
    {
        var calls = 0;
        var handler = new ScriptedHandler(_ => ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)529) : Json(SampleResponse));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key", TimeSpan.Zero);

        Assert.Equal(0.97, (await classifier.ClassifyAsync("Text", CancellationToken.None)).AsksUser);
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
    [InlineData("{\"answers\":{\"asks_user\":{\"type\":\"noul\",\"noul\":\"high\"}}}")]
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
        var handler = new ScriptedHandler(_ => Json(SampleResponse.Replace("0.97", "1.4")));
        var classifier = new JevTurnEndClassifier(new HttpClient(handler), "key");

        Assert.Equal(1, (await classifier.ClassifyAsync("Text", CancellationToken.None)).AsksUser);
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
