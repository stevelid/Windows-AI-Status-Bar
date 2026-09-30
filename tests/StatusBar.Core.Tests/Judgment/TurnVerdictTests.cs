using StatusBar.Core.Claude;
using StatusBar.Core.Codex;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Judgment;

public sealed class TurnVerdictPolicyTests
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    static TurnVerdict Verdict(double alert, double report = 0, double offer = 0, TurnEndKind alertKind = TurnEndKind.WaitingForAnswer) =>
        new(TurnVerdictPolicy.EvidenceKeyFor(At), new TurnEndJudgment(TurnEndKind.Finished, alertKind, alert, report, offer, 0, 0, 1, 1));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Without_captured_text_the_rules_decide(bool rules, bool _)
    {
        Assert.Equal(rules, TurnVerdictPolicy.AsksUser(rules, null, null, At));
        Assert.Null(TurnVerdictPolicy.Detail(null, Verdict(0.1, report: 0.9)));
    }

    [Fact]
    public void While_the_answer_is_awaited_the_turn_is_quiet_then_the_rules_take_over()
    {
        Assert.Null(TurnVerdictPolicy.AsksUser(true, At, null, At.AddSeconds(2)));
        Assert.True(TurnVerdictPolicy.AsksUser(true, At, null, At + TurnVerdictPolicy.Grace));
        Assert.False(TurnVerdictPolicy.AsksUser(false, At, null, At + TurnVerdictPolicy.Grace));
    }

    [Theory]
    [InlineData(0.9, false, true)]
    [InlineData(0.5, false, true)]
    [InlineData(0.49, true, false)]
    [InlineData(0.1, true, false)]
    public void A_matching_answer_overrides_the_rules_in_both_directions(double asks, bool rules, bool expected) =>
        Assert.Equal(expected, TurnVerdictPolicy.AsksUser(rules, At, Verdict(asks), At.AddSeconds(1)));

    [Fact]
    public void A_null_answer_means_the_rules_decide_at_once()
    {
        var fellBack = new TurnVerdict(TurnVerdictPolicy.EvidenceKeyFor(At), null);

        Assert.True(TurnVerdictPolicy.AsksUser(true, At, fellBack, At.AddSeconds(1)));
        Assert.False(TurnVerdictPolicy.AsksUser(false, At, fellBack, At.AddSeconds(1)));
        Assert.Null(TurnVerdictPolicy.Detail(At, fellBack));
    }

    [Fact]
    public void Broadly_asking_for_input_does_not_raise_an_alert_unless_the_assistant_is_stopped()
    {
        var reportWithNextSteps = new TurnVerdict(
            TurnVerdictPolicy.EvidenceKeyFor(At),
            new TurnEndJudgment(TurnEndKind.ReportWithNextSteps, TurnEndKind.WaitingForAnswer, Alert: 0.14, Report: 0.9, Offer: 0.02, Blocked: 0.29, Asks: 0.92, 1, 1));

        Assert.False(TurnVerdictPolicy.AsksUser(false, At, reportWithNextSteps, At.AddSeconds(1)));
        Assert.Equal(TurnVerdictPolicy.NextStepsDetail, TurnVerdictPolicy.Detail(At, reportWithNextSteps));
    }

    [Theory]
    [InlineData(TurnEndKind.WaitingForAnswer, TurnVerdictPolicy.AnswerReason)]
    [InlineData(TurnEndKind.WaitingForApproval, TurnVerdictPolicy.ApprovalReason)]
    [InlineData(TurnEndKind.Stuck, TurnVerdictPolicy.StuckReason)]
    public void The_alert_wording_follows_the_kind_of_stop(TurnEndKind kind, string expected)
    {
        Assert.Equal(expected, TurnVerdictPolicy.AttentionReason(At, Verdict(0.9, alertKind: kind)));
        Assert.True(TurnVerdictPolicy.IsQuestionReason(expected));
    }

    [Fact]
    public void Without_an_answer_the_rules_wording_is_used_and_it_still_counts_as_a_question()
    {
        Assert.Equal(TurnVerdictPolicy.RulesQuestionReason, TurnVerdictPolicy.AttentionReason(At, null));
        Assert.Equal(TurnVerdictPolicy.RulesQuestionReason, TurnVerdictPolicy.AttentionReason(null, Verdict(0.9)));
        Assert.True(TurnVerdictPolicy.IsQuestionReason(TurnVerdictPolicy.RulesQuestionReason));
        Assert.False(TurnVerdictPolicy.IsQuestionReason("Permission requested"));
        Assert.False(TurnVerdictPolicy.IsQuestionReason(null));
    }

    [Fact]
    public void An_answer_for_an_earlier_turn_is_ignored()
    {
        var stale = new TurnVerdict(TurnVerdictPolicy.EvidenceKeyFor(At.AddMinutes(-5)), Verdict(0.9, report: 0.9).Judgment);

        Assert.Null(TurnVerdictPolicy.AsksUser(false, At, stale, At.AddSeconds(1)));
        Assert.Null(TurnVerdictPolicy.Detail(At, stale));
    }

    [Theory]
    [InlineData(0.1, 0.9, 0.9, TurnVerdictPolicy.NextStepsDetail)]
    [InlineData(0.1, 0.59, 0.6, TurnVerdictPolicy.FollowUpDetail)]
    [InlineData(0.1, 0.2, 0.2, null)]
    [InlineData(0.9, 0.9, 0.9, null)]
    public void Next_steps_outrank_an_offer_both_need_the_threshold_and_a_stopped_turn_gets_no_hint(double alert, double report, double offer, string? expected) =>
        Assert.Equal(expected, TurnVerdictPolicy.Detail(At, Verdict(alert, report, offer)));
}

[Collection("FinalMessageCapture")]
public sealed class TurnVerdictProviderTests : IDisposable
{
    const string Question = "Placeholder final message. Shall I continue with the sample?";
    const string Statement = "Placeholder final message. The sample is finished.";
    readonly string _root = Path.Combine(Path.GetTempPath(), "statusbar-verdict-" + Guid.NewGuid().ToString("N"));

    public TurnVerdictProviderTests() => FinalMessageCapture.Enabled = true;

    public void Dispose()
    {
        FinalMessageCapture.Enabled = false;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    static TurnEndJudgment Judgment(double alert, double report = 0, double offer = 0, TurnEndKind alertKind = TurnEndKind.WaitingForAnswer) =>
        new(TurnEndKind.Finished, alertKind, alert, report, offer, alert, alert, 1, 1);

    [Theory]
    [InlineData(Question, 0.95, AgentTaskStatus.NeedsAttention)]
    [InlineData(Question, 0.05, AgentTaskStatus.Complete)]
    [InlineData(Statement, 0.95, AgentTaskStatus.NeedsAttention)]
    [InlineData(Statement, 0.05, AgentTaskStatus.Complete)]
    public async Task Codex_follows_the_answer_over_the_rules(string text, double asks, AgentTaskStatus expected)
    {
        var (provider, file, seen) = await StartCodexAsync();
        Append(file, CodexComplete(text));
        await provider.ReconcileAsync();
        var info = Assert.Single(seen);

        // Held quietly while the answer is awaited, even when the rules say it asked a question.
        Assert.Equal(AgentTaskStatus.Complete, Assert.Single(provider.Current.Tasks).Status);

        await provider.ApplyTurnJudgmentAsync(info, Judgment(asks));

        var task = Assert.Single(provider.Current.Tasks);
        Assert.Equal(expected, task.Status);
        if (expected == AgentTaskStatus.NeedsAttention) Assert.Equal(TurnVerdictPolicy.AnswerReason, task.AttentionReason);
        await provider.DisposeAsync();
    }

    [Theory]
    [InlineData(Question, AgentTaskStatus.NeedsAttention)]
    [InlineData(Statement, AgentTaskStatus.Complete)]
    public async Task Codex_falls_back_to_the_rules_when_the_answer_is_null(string text, AgentTaskStatus expected)
    {
        var (provider, file, seen) = await StartCodexAsync();
        Append(file, CodexComplete(text));
        await provider.ReconcileAsync();

        await provider.ApplyTurnJudgmentAsync(Assert.Single(seen), null);

        Assert.Equal(expected, Assert.Single(provider.Current.Tasks).Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Codex_shows_a_next_steps_hint_on_a_finished_turn()
    {
        var (provider, file, seen) = await StartCodexAsync();
        Append(file, CodexComplete(Statement));
        await provider.ReconcileAsync();

        await provider.ApplyTurnJudgmentAsync(Assert.Single(seen), Judgment(alert: 0.05, report: 0.9));

        var task = Assert.Single(provider.Current.Tasks);
        Assert.Equal(AgentTaskStatus.Complete, task.Status);
        Assert.Equal(TurnVerdictPolicy.NextStepsDetail, task.StatusDetail);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task An_answer_for_a_turn_that_has_since_restarted_is_dropped()
    {
        var (provider, file, seen) = await StartCodexAsync();
        Append(file, CodexComplete(Question));
        await provider.ReconcileAsync();
        var info = Assert.Single(seen);
        Append(file, "{\"timestamp\":\"" + Now() + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}");
        await provider.ReconcileAsync();

        await provider.ApplyTurnJudgmentAsync(info, Judgment(0.95));

        Assert.Equal(AgentTaskStatus.Working, Assert.Single(provider.Current.Tasks).Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Claude_follows_the_answer_over_the_rules()
    {
        var paths = ClaudeCodePaths.Resolve(overrideHome: _root);
        var file = Path.Combine(paths.ProjectsDirectory, "project-folder", "0199a000-0000-7000-8000-00000000dcba.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Append(file, "{\"type\":\"user\",\"sessionId\":\"0199a000-0000-7000-8000-00000000dcba\",\"timestamp\":\"" + Now() + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Sample request\"}]}}");
        var seen = new List<TurnEndInfo>();
        await using var provider = new ClaudeCodeTaskProvider(paths, null, TimeProvider.System, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();

        Append(file, "{\"type\":\"assistant\",\"sessionId\":\"0199a000-0000-7000-8000-00000000dcba\",\"timestamp\":\"" + Now() + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + Statement + "\"}],\"stop_reason\":\"end_turn\"}}");
        await provider.ReconcileAsync();
        var info = Assert.Single(seen);
        Assert.Equal(AgentTaskStatus.Complete, Assert.Single(provider.Current.Tasks).Status);

        await provider.ApplyTurnJudgmentAsync(info, Judgment(0.9));
        Assert.Equal(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);
    }

    async Task<(CodexTaskProvider Provider, string File, List<TurnEndInfo> Seen)> StartCodexAsync()
    {
        var paths = CodexPaths.Resolve(overrideHome: _root);
        var today = DateTime.Now;
        var file = Path.Combine(paths.SessionsDirectory, today.Year.ToString("D4"), today.Month.ToString("D2"), today.Day.ToString("D2"),
            "rollout-2026-01-01T00-00-00-0199a000-0000-7000-8000-00000000abcd.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Append(file, "{\"timestamp\":\"" + Now() + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"0199a000-0000-7000-8000-00000000abcd\",\"source\":\"vscode\"}}");
        Append(file, "{\"timestamp\":\"" + Now() + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}");
        var seen = new List<TurnEndInfo>();
        var provider = new CodexTaskProvider(paths, TimeProvider.System, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();
        return (provider, file, seen);
    }

    static string CodexComplete(string text) =>
        "{\"timestamp\":\"" + Now() + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"" + text + "\"}}";

    static void Append(string file, string line) => File.AppendAllText(file, line + "\n");
}
