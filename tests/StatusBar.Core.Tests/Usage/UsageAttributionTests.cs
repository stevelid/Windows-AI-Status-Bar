using StatusBar.Core.Tasks;
using StatusBar.Core.Usage;

namespace StatusBar.Core.Tests.Usage;

public sealed class UsageAttributionTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    static readonly TimeSpan LookBack = TimeSpan.FromHours(3);

    static (DateTimeOffset At, double Remaining)[] Readings(params (int MinutesAgo, double Remaining)[] points) =>
        points.Select(point => (Now.AddMinutes(-point.MinutesAgo), point.Remaining)).ToArray();

    static ActivityInterval Working(string id, int startMinutesAgo, int? endMinutesAgo, string title = "Sample task") =>
        new(id, AgentProvider.Codex, title, Now.AddMinutes(-startMinutesAgo), endMinutesAgo is null ? null : Now.AddMinutes(-endMinutesAgo.Value));

    [Fact]
    public void One_conversation_gets_the_whole_drop_while_it_worked()
    {
        var readings = Readings((30, 80), (20, 78), (10, 74), (0, 74));

        var result = UsageAttributionCalculator.Compute(readings, [Working("codex:a", 30, 10)], Now, LookBack);

        Assert.Equal(6, result.TotalPoints, precision: 6);
        Assert.Equal(6, result.PointsByTask["codex:a"], precision: 6);
        Assert.Equal(0, result.UnattributedPoints);
    }

    [Fact]
    public void A_drop_is_shared_between_conversations_in_proportion_to_their_time()
    {
        // Two readings 10 minutes apart drop 6 points. "a" worked the whole 10 minutes, "b" only the last 5.
        var readings = Readings((10, 50), (0, 44));
        var intervals = new[] { Working("codex:a", 10, null), Working("codex:b", 5, null) };

        var result = UsageAttributionCalculator.Compute(readings, intervals, Now, LookBack);

        Assert.Equal(4, result.PointsByTask["codex:a"], precision: 6);
        Assert.Equal(2, result.PointsByTask["codex:b"], precision: 6);
        Assert.Equal(0, result.UnattributedPoints);
    }

    [Fact]
    public void Use_while_nothing_was_running_is_unattributed()
    {
        var readings = Readings((30, 90), (20, 88), (10, 85), (0, 85));

        var result = UsageAttributionCalculator.Compute(readings, [Working("codex:a", 12, 5)], Now, LookBack);

        // The 30-20 drop (2) and the 20-10 drop (3, of which the interval only starts at -12) are mostly unexplained.
        Assert.Equal(5, result.TotalPoints, precision: 6);
        Assert.Equal(2, result.UnattributedPoints, precision: 6);
        Assert.Equal(3, result.PointsByTask["codex:a"], precision: 6);
    }

    [Fact]
    public void Several_turns_of_one_conversation_add_up()
    {
        var readings = Readings((60, 80), (50, 76), (40, 76), (30, 76), (20, 72), (10, 72));
        var intervals = new[] { Working("codex:a", 60, 50), Working("codex:a", 30, 20) };

        var result = UsageAttributionCalculator.Compute(readings, intervals, Now, LookBack);

        Assert.Equal(8, result.PointsByTask["codex:a"], precision: 6);
    }

    [Fact]
    public void Resets_gaps_and_old_readings_are_not_counted()
    {
        var readings = Readings(
            (240, 90), (235, 80),          // older than the look-back
            (100, 30), (98, 100),          // a reset: allowance came back
            (96, 99),                      // then a real 1-point drop
            (60, 90), (20, 70));           // 40 minutes apart: a gap, not a burst

        var result = UsageAttributionCalculator.Compute(readings, [Working("codex:a", 200, null)], Now, LookBack);

        Assert.Equal(1, result.TotalPoints, precision: 6);
        Assert.Equal(1, result.PointsByTask["codex:a"], precision: 6);
    }

    [Fact]
    public void No_readings_or_no_conversations_give_an_empty_result()
    {
        Assert.Equal(0, UsageAttributionCalculator.Compute([], [], Now, LookBack).TotalPoints);
        var result = UsageAttributionCalculator.Compute(Readings((10, 50), (0, 48)), [], Now, LookBack);
        Assert.Empty(result.PointsByTask);
        Assert.Equal(2, result.UnattributedPoints, precision: 6);
    }
}
