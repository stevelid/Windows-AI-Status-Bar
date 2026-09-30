using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Judgment;

public sealed class TurnEndMonitorTests
{
    const string SecretText = "Placeholder final message that must never reach the log.";

    [Fact]
    public async Task Logs_agreement_with_the_rules_without_any_message_text()
    {
        var log = new LogSink();
        var classifier = new StubClassifier(Judge(TurnEndKind.WaitingForAnswer, alert: 0.97, report: 0.12, offer: 0.05, blocked: 0.93, asks: 0.91, input: 300, output: 18));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, new FakeTimeProvider());

        monitor.Observe(Info("codex:1a2b3c4d", "t1", "question"));
        var line = await log.NextAsync();

        Assert.Contains("codex:1a2b3c4d", line, StringComparison.Ordinal);
        Assert.Contains("rules=question", line, StringComparison.Ordinal);
        Assert.Contains("kind=waiting_for_answer alert=0.97 report=0.12 offer=0.05 | blocked=0.93 asks=0.91", line, StringComparison.Ordinal);
        Assert.Contains($"Jev v{JevTurnEndClassifier.PromptVersion} codex:1a2b3c4d", line, StringComparison.Ordinal);
        Assert.Contains("agree=yes", line, StringComparison.Ordinal);
        Assert.Contains("300+18 tokens", line, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretText, line, StringComparison.Ordinal);
        var stats = monitor.Stats;
        Assert.Equal((1, 1, 0), (stats.Judged, stats.Agreed, stats.Disagreed));
    }

    [Fact]
    public async Task A_report_that_asks_for_input_but_is_not_blocked_is_not_a_question()
    {
        // The case that prompted the rewording: the broad "asks" score is high, the alert score is low.
        var log = new LogSink();
        var classifier = new StubClassifier(Judge(TurnEndKind.ReportWithNextSteps, alert: 0.12, report: 0.9, blocked: 0.12, asks: 0.95));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, new FakeTimeProvider());

        monitor.Observe(Info("claude:1c8e6886", "t1", "none"));

        var line = await log.NextAsync();
        Assert.Contains("kind=report_with_next_steps alert=0.12", line, StringComparison.Ordinal);
        Assert.Contains("blocked=0.12 asks=0.95", line, StringComparison.Ordinal);
        Assert.Contains("agree=yes", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", 0.9, "NO")]
    [InlineData("question", 0.1, "NO")]
    [InlineData("none", 0.1, "yes")]
    [InlineData("card", 0.1, "n/a (card)")]
    public async Task Compares_the_rules_with_the_half_probability_line(string rules, double asks, string expected)
    {
        var log = new LogSink();
        var classifier = new StubClassifier(Judge(alert: asks));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, new FakeTimeProvider());

        monitor.Observe(Info("claude:aaaaaaaa", "t1", rules));

        Assert.Contains("agree=" + expected, await log.NextAsync(), StringComparison.Ordinal);
        var stats = monitor.Stats;
        Assert.Equal(expected == "yes" ? 1 : 0, stats.Agreed);
        Assert.Equal(expected == "NO" ? 1 : 0, stats.Disagreed);
        Assert.Equal(expected.StartsWith("n/a", StringComparison.Ordinal) ? 1 : 0, stats.StructuredQuestions);
    }

    [Fact]
    public async Task Judges_each_turn_once_and_does_nothing_while_the_feature_is_off()
    {
        var log = new LogSink();
        var classifier = new StubClassifier(Judge());
        ITurnEndClassifier? current = null;
        using var monitor = new TurnEndMonitor(() => current, log.Write, new FakeTimeProvider());

        monitor.Observe(Info("codex:1a2b3c4d", "t1", "none"));
        current = classifier;
        monitor.Observe(Info("codex:1a2b3c4d", "t1", "none"));
        await log.NextAsync();
        monitor.Observe(Info("codex:1a2b3c4d", "t1", "none"));
        monitor.Observe(Info("codex:1a2b3c4d", "t2", "none"));
        await log.NextAsync();

        Assert.Equal(2, classifier.Calls);
        Assert.Equal(2, monitor.Stats.Judged);
    }

    [Fact]
    public async Task A_failed_call_is_logged_by_code_only_and_repeated_failures_pause_the_checks()
    {
        var log = new LogSink();
        var time = new FakeTimeProvider();
        var classifier = new StubClassifier(error: new TurnEndClassifierException("Http401", System.Net.HttpStatusCode.Unauthorized));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, time);

        for (var i = 0; i < 5; i++)
        {
            monitor.Observe(Info("codex:1a2b3c4d", "t" + i, "none"));
            Assert.Contains("failed (Http401)", await log.NextAsync(), StringComparison.Ordinal);
        }
        Assert.Contains("paused", await log.NextAsync(), StringComparison.Ordinal);

        monitor.Observe(Info("codex:1a2b3c4d", "later", "none"));
        Assert.Equal(1, monitor.Stats.Skipped);
        Assert.Equal(5, classifier.Calls);

        time.Advance(TimeSpan.FromMinutes(6));
        monitor.Observe(Info("codex:1a2b3c4d", "after-pause", "none"));
        Assert.Contains("failed", await log.NextAsync(), StringComparison.Ordinal);
        Assert.Equal(6, monitor.Stats.Failed);
    }

    [Fact]
    public async Task An_unexpected_exception_type_is_logged_by_name_not_message()
    {
        var log = new LogSink();
        var classifier = new StubClassifier(error: new InvalidOperationException("secret detail"));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, new FakeTimeProvider());

        monitor.Observe(Info("codex:1a2b3c4d", "t1", "none"));
        var line = await log.NextAsync();

        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "question", "applied=jev")]
    [InlineData(false, "question", "applied=rules")]
    [InlineData(true, "card", "applied=rules")]
    public async Task Every_turn_is_returned_to_the_provider_and_the_log_says_who_decided(bool affects, string rules, string expected)
    {
        var log = new LogSink();
        var returned = new BlockingCollection<(TurnEndInfo Info, TurnEndJudgment? Judgment)>();
        var judgment = Judge(alert: 0.2, input: 5, output: 5);
        using var monitor = new TurnEndMonitor(
            () => new StubClassifier(judgment), log.Write, new FakeTimeProvider(),
            (info, result) => returned.Add((info, result)), () => affects);

        monitor.Observe(Info("codex:1a2b3c4d", "t1", rules));

        Assert.Contains(expected, await log.NextAsync(), StringComparison.Ordinal);
        Assert.True(returned.TryTake(out var item, TimeSpan.FromSeconds(10)));
        Assert.Equal("t1", item.Info.EvidenceKey);
        Assert.Equal(judgment, item.Judgment);
    }

    [Fact]
    public async Task A_failed_or_paused_check_returns_null_so_the_rules_decide()
    {
        var log = new LogSink();
        var returned = new BlockingCollection<TurnEndJudgment?>();
        var time = new FakeTimeProvider();
        var classifier = new StubClassifier(error: new TurnEndClassifierException("Timeout"));
        using var monitor = new TurnEndMonitor(() => classifier, log.Write, time, (_, result) => returned.Add(result));

        for (var i = 0; i < 5; i++)
        {
            monitor.Observe(Info("codex:1a2b3c4d", "t" + i, "none"));
            await log.NextAsync();
        }
        await log.NextAsync(); // "paused"
        monitor.Observe(Info("codex:1a2b3c4d", "while-paused", "none"));

        Assert.Equal(6, returned.Count);
        Assert.All(returned, result => Assert.Null(result));
    }

    static TurnEndJudgment Judge(
        TurnEndKind kind = TurnEndKind.Finished,
        double alert = 0,
        double report = 0,
        double offer = 0,
        double blocked = 0,
        double asks = 0,
        int input = 1,
        int output = 1) =>
        new(kind, alert >= 0.5 ? TurnEndKind.WaitingForAnswer : TurnEndKind.Stuck, alert, report, offer, blocked, asks, input, output);

    static TurnEndInfo Info(string key, string evidence, string rules) =>
        new(key.StartsWith("codex", StringComparison.Ordinal) ? AgentProvider.Codex : AgentProvider.Claude, key, evidence, rules, SecretText);

    sealed class StubClassifier(TurnEndJudgment? judgment = null, Exception? error = null) : ITurnEndClassifier
    {
        int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<TurnEndJudgment> ClassifyAsync(string finalMessage, CancellationToken cancellationToken, string? messageStart = null)
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal(SecretText, finalMessage);
            return error is not null ? Task.FromException<TurnEndJudgment>(error) : Task.FromResult(judgment!);
        }
    }

    sealed class LogSink
    {
        readonly BlockingCollection<string> _lines = [];

        public void Write(string line) => _lines.Add(line);

        public Task<string> NextAsync() => Task.Run(() =>
            _lines.TryTake(out var line, TimeSpan.FromSeconds(10))
                ? line
                : throw new TimeoutException("No log line arrived."));
    }
}
