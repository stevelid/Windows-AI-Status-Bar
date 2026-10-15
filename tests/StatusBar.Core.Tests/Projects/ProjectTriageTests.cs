using StatusBar.Core.Projects;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectTriageTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");

    [Theory]
    [InlineData(true, true, 3, ProjectSection.NeedsYou)]
    [InlineData(false, true, 3, ProjectSection.Working)]
    [InlineData(false, false, 2, ProjectSection.New)]
    [InlineData(false, false, 0, ProjectSection.Earlier)]
    public void Section_follows_attention_priority(bool input, bool working, int unseen, ProjectSection expected)
    {
        Assert.Equal(expected, ProjectTriage.SectionOf(Item("p", working, input, unseen)));
    }

    [Fact]
    public void Group_returns_fixed_section_order_and_keeps_input_order()
    {
        var items = new[]
        {
            Item("e1", false, false, 0), Item("w1", true, false, 0), Item("n1", false, false, 1),
            Item("a1", false, true, 0), Item("w2", true, false, 0), Item("e2", false, false, 0),
        };

        var groups = ProjectTriage.Group(items);

        Assert.Equal([ProjectSection.NeedsYou, ProjectSection.Working, ProjectSection.New, ProjectSection.Earlier],
            groups.Select(group => group.Section));
        Assert.Equal(["a1"], groups[0].Items.Select(item => item.Project.Id));
        Assert.Equal(["w1", "w2"], groups[1].Items.Select(item => item.Project.Id));
        Assert.Equal(["n1"], groups[2].Items.Select(item => item.Project.Id));
        Assert.Equal(["e1", "e2"], groups[3].Items.Select(item => item.Project.Id));
    }

    [Fact]
    public void Group_keeps_empty_sections_so_callers_can_skip_them()
    {
        var groups = ProjectTriage.Group([]);
        Assert.Equal(4, groups.Count);
        Assert.All(groups, group => Assert.Empty(group.Items));
    }

    [Fact]
    public void LatestUpdate_prefers_needs_input_then_newest()
    {
        var question = Update("u1", 0, needsInput: true);
        var otherThread = Update("u2", 5, needsInput: false) with { ThreadId = "codex:other" };
        Assert.Equal("u1", ProjectTriage.LatestUpdate(Item("p", false, true, 1, question, otherThread))!.Id);
        Assert.Equal("u2", ProjectTriage.LatestUpdate(Item("p", false, false, 1, question, otherThread))!.Id);
        // A question superseded in its own thread is not open even if the project flag is set.
        var newer = Update("u3", 9, needsInput: false);
        Assert.Equal("u3", ProjectTriage.LatestUpdate(Item("p", false, true, 1, question, newer))!.Id);
    }

    [Fact]
    public void LatestUpdate_is_null_without_updates()
    {
        Assert.Null(ProjectTriage.LatestUpdate(Item("p", false, false, 0)));
    }

    [Fact]
    public void AcknowledgeAll_covers_every_current_revision_and_drops_stale_entries()
    {
        var document = new ProjectOverviewDocument
        {
            Updates = [Update("u1", 0, false) with { Revision = 2 }, Update("u2", 1, false)],
            Acknowledgements = [new ProjectAcknowledgement("u1", 1), new ProjectAcknowledgement("gone", 1)],
        };

        var result = ProjectOverviewDocumentRules.AcknowledgeAll(document);

        Assert.Equal(
            [new ProjectAcknowledgement("u1", 2), new ProjectAcknowledgement("u2", 1)],
            result.Acknowledgements);
    }

    [Fact]
    public void AcknowledgeAll_is_idempotent()
    {
        var document = new ProjectOverviewDocument { Updates = [Update("u1", 0, false)] };
        var once = ProjectOverviewDocumentRules.AcknowledgeAll(document);
        Assert.Equal(once.Acknowledgements, ProjectOverviewDocumentRules.AcknowledgeAll(once).Acknowledgements);
    }

    static ProjectOverviewItem Item(string id, bool working, bool input, int unseen, params ProjectUpdate[] updates) =>
        new(new ProjectDefinition { Id = id, Name = id }, [], updates, working, input, unseen, 0);

    static ProjectUpdate Update(string id, int minutes, bool needsInput) => new()
    {
        Id = id,
        ProjectId = "p",
        ThreadId = "codex:t",
        Revision = 1,
        OccurredAt = Now.AddMinutes(minutes),
        LatestAction = "Made a fictional change",
        NeedsInput = needsInput,
    };
}
