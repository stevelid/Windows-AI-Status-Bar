using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Claude;
using StatusBar.Core.Codex;
using StatusBar.Core.Judgment;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Judgment;

public sealed class StartupJudgmentPolicyTests
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

    [Fact]
    public void A_recovered_turn_waits_the_grace_from_when_its_answer_was_asked_for()
    {
        var requested = At.AddMinutes(10);

        // Without the request time the old turn would fall straight to the rules.
        Assert.True(TurnVerdictPolicy.AsksUser(true, At, null, requested.AddSeconds(1)));
        Assert.Null(TurnVerdictPolicy.AsksUser(true, At, null, requested.AddSeconds(1), requested));
        Assert.True(TurnVerdictPolicy.AsksUser(true, At, null, requested + TurnVerdictPolicy.Grace, requested));
        Assert.False(TurnVerdictPolicy.AsksUser(false, At, null, requested + TurnVerdictPolicy.Grace, requested));
    }

    [Fact]
    public void A_request_time_earlier_than_the_final_message_changes_nothing()
    {
        Assert.Null(TurnVerdictPolicy.AsksUser(true, At, null, At.AddSeconds(2), At.AddMinutes(-5)));
        Assert.True(TurnVerdictPolicy.AsksUser(true, At, null, At + TurnVerdictPolicy.Grace, At.AddMinutes(-5)));
    }

    [Fact]
    public void Start_up_judging_takes_only_recent_turns_newest_first_and_caps_them()
    {
        var now = At;
        var times = new List<DateTimeOffset> { now - TurnVerdictPolicy.StartupJudgmentWindow - TimeSpan.FromSeconds(1) };
        for (var i = 0; i < 15; i++) times.Add(now.AddMinutes(-1 - i));

        var chosen = TurnVerdictPolicy.SelectStartupJudgments(times, now);

        Assert.Equal(TurnVerdictPolicy.MaximumStartupJudgments, chosen.Count);
        Assert.DoesNotContain(0, chosen);
        Assert.Equal(Enumerable.Range(1, 10), chosen);
    }

    [Fact]
    public void Start_up_judging_skips_everything_when_all_turns_are_stale()
    {
        Assert.Empty(TurnVerdictPolicy.SelectStartupJudgments([At.AddHours(-2), At.AddDays(-1)], At));
    }
}

[Collection("FinalMessageCapture")]
public sealed class StartupJudgmentProviderTests : IDisposable
{
    const string Message = "Placeholder final message. Shall I continue with the sample?";
    readonly string _root = Path.Combine(Path.GetTempPath(), "statusbar-startup-" + Guid.NewGuid().ToString("N"));

    public StartupJudgmentProviderTests() => FinalMessageCapture.Enabled = true;

    public void Dispose()
    {
        FinalMessageCapture.Enabled = false;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    static TurnEndJudgment Judgment(double alert, double urgent = 1) =>
        new(TurnEndKind.Finished, TurnEndKind.WaitingForAnswer, alert, 0, 0, 1, 0, urgent, 1, 1);

    string WriteCodex(DateTimeOffset now, string id, DateTimeOffset finishedAt)
    {
        var local = DateTime.Now;
        var file = Path.Combine(CodexPaths.Resolve(overrideHome: _root).SessionsDirectory, local.Year.ToString("D4"), local.Month.ToString("D2"), local.Day.ToString("D2"),
            $"rollout-2026-01-01T00-00-00-{id}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            $"{{\"timestamp\":\"{Stamp(finishedAt.AddSeconds(-2))}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"source\":\"vscode\"}}}}\n" +
            $"{{\"timestamp\":\"{Stamp(finishedAt.AddSeconds(-1))}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\"}}}}\n" +
            $"{{\"timestamp\":\"{Stamp(finishedAt)}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_complete\",\"last_agent_message\":\"{Message}\"}}}}\n");
        return file;
    }

    string WriteClaude(string id, DateTimeOffset finishedAt)
    {
        var file = Path.Combine(ClaudeCodePaths.Resolve(overrideHome: _root).ProjectsDirectory, "project-folder", id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            $"{{\"type\":\"user\",\"sessionId\":\"{id}\",\"timestamp\":\"{Stamp(finishedAt.AddSeconds(-1))}\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"Sample request\"}}]}}}}\n" +
            $"{{\"type\":\"assistant\",\"sessionId\":\"{id}\",\"timestamp\":\"{Stamp(finishedAt)}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{Message}\"}}],\"stop_reason\":\"end_turn\"}}}}\n");
        return file;
    }

    [Fact]
    public async Task Codex_judges_a_recent_finished_turn_on_first_read_and_holds_it_quietly_until_answered()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteCodex(time.GetUtcNow(), "0199a000-0000-7000-8000-00000000abcd", time.GetUtcNow().AddMinutes(-5));
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(CodexPaths.Resolve(overrideHome: _root), time, watchFiles: false);
        provider.TurnEnded += seen.Add;

        await provider.ReconcileAsync();

        var info = Assert.Single(seen);
        Assert.Equal("codex:0000abcd", info.TaskKey);
        Assert.Equal("question", info.Heuristic);
        // Held quietly: the rules' alert is not flashed while Jev is asked.
        Assert.NotEqual(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);

        await provider.ApplyTurnJudgmentAsync(info, Judgment(alert: 0.9, urgent: 0.1));
        var task = Assert.Single(provider.Current.Tasks);
        Assert.NotEqual(AgentTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(TurnVerdictPolicy.GoAheadDetail, task.StatusDetail);

        // The same turn is not raised again.
        await provider.ReconcileAsync();
        Assert.Single(seen);
    }

    [Fact]
    public async Task Codex_falls_back_to_the_rules_when_the_recovered_turns_answer_is_null()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteCodex(time.GetUtcNow(), "0199a000-0000-7000-8000-00000000abcd", time.GetUtcNow().AddMinutes(-5));
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(CodexPaths.Resolve(overrideHome: _root), time, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();

        await provider.ApplyTurnJudgmentAsync(Assert.Single(seen), null);

        Assert.Equal(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);
    }

    [Fact]
    public async Task Codex_falls_back_to_the_rules_when_no_answer_arrives_within_the_grace()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteCodex(time.GetUtcNow(), "0199a000-0000-7000-8000-00000000abcd", time.GetUtcNow().AddMinutes(-5));
        await using var provider = new CodexTaskProvider(CodexPaths.Resolve(overrideHome: _root), time, watchFiles: false);
        await provider.ReconcileAsync();
        Assert.NotEqual(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);

        time.Advance(TurnVerdictPolicy.Grace + TimeSpan.FromSeconds(1));
        await provider.ReconcileAsync();

        Assert.Equal(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);
    }

    [Fact]
    public async Task Codex_does_not_judge_a_turn_that_finished_over_thirty_minutes_ago()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteCodex(time.GetUtcNow(), "0199a000-0000-7000-8000-00000000abcd", time.GetUtcNow().AddMinutes(-31));
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(CodexPaths.Resolve(overrideHome: _root), time, watchFiles: false);
        provider.TurnEnded += seen.Add;

        await provider.ReconcileAsync();

        Assert.Empty(seen);
    }

    [Fact]
    public async Task Codex_judges_only_the_ten_most_recent_turns_at_start_up()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        for (var i = 0; i < 14; i++)
            WriteCodex(time.GetUtcNow(), $"0199a000-0000-7000-8000-0000000{i:D5}"[..36], time.GetUtcNow().AddMinutes(-1 - i));
        var seen = new List<TurnEndInfo>();
        await using var provider = new CodexTaskProvider(CodexPaths.Resolve(overrideHome: _root), time, watchFiles: false);
        provider.TurnEnded += seen.Add;

        await provider.ReconcileAsync();

        Assert.Equal(TurnVerdictPolicy.MaximumStartupJudgments, seen.Count);
        Assert.Equal(TurnVerdictPolicy.MaximumStartupJudgments, seen.Select(info => info.TaskKey).Distinct().Count());
    }

    [Fact]
    public async Task Claude_judges_a_recent_finished_turn_on_first_read_and_holds_it_quietly_until_answered()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteClaude("0199a000-0000-7000-8000-00000000dcba", time.GetUtcNow().AddMinutes(-5));
        var seen = new List<TurnEndInfo>();
        await using var provider = new ClaudeCodeTaskProvider(ClaudeCodePaths.Resolve(overrideHome: _root), null, time, watchFiles: false);
        provider.TurnEnded += seen.Add;

        await provider.ReconcileAsync();

        var info = Assert.Single(seen);
        Assert.Equal("claude:0000dcba", info.TaskKey);
        Assert.NotEqual(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);

        await provider.ApplyTurnJudgmentAsync(info, Judgment(alert: 0.9, urgent: 0.1));
        var task = Assert.Single(provider.Current.Tasks);
        Assert.NotEqual(AgentTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(TurnVerdictPolicy.GoAheadDetail, task.StatusDetail);
    }

    [Fact]
    public async Task Claude_falls_back_to_the_rules_when_the_recovered_turns_answer_is_null()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteClaude("0199a000-0000-7000-8000-00000000dcba", time.GetUtcNow().AddMinutes(-5));
        var seen = new List<TurnEndInfo>();
        await using var provider = new ClaudeCodeTaskProvider(ClaudeCodePaths.Resolve(overrideHome: _root), null, time, watchFiles: false);
        provider.TurnEnded += seen.Add;
        await provider.ReconcileAsync();

        await provider.ApplyTurnJudgmentAsync(Assert.Single(seen), null);

        Assert.Equal(AgentTaskStatus.NeedsAttention, Assert.Single(provider.Current.Tasks).Status);
    }

    [Fact]
    public async Task Claude_does_not_judge_a_turn_that_finished_over_thirty_minutes_ago()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        WriteClaude("0199a000-0000-7000-8000-00000000dcba", time.GetUtcNow().AddMinutes(-31));
        var seen = new List<TurnEndInfo>();
        await using var provider = new ClaudeCodeTaskProvider(ClaudeCodePaths.Resolve(overrideHome: _root), null, time, watchFiles: false);
        provider.TurnEnded += seen.Add;

        await provider.ReconcileAsync();

        Assert.Empty(seen);
    }
}
