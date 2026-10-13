using System.Text.Json;
using StatusBar.Core.Projects;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectOverviewDocumentTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");

    [Fact]
    public void Versioned_document_round_trips_derived_text_and_coverage()
    {
        var document = new ProjectOverviewDocument
        {
            Projects = [new ProjectDefinition { Id = "p1", Name = "Fictional project", CurrentSummary = "Prepared the sample" }],
            Memberships = [new ProjectMembership("p1", "codex:thread-1", ProjectMembershipSource.Manual)],
            Updates = [Update("u1", "p1", "codex:thread-1", Now)],
            Acknowledgements = [new ProjectAcknowledgement("u1", 1)],
            Coverage = [new ProjectCoverageCheckpoint("p1", "codex:thread-1", "turn-1", Now, "1", "prompt-1", "gpt-6-luna")],
        };

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ProjectOverviewDocument>(json);

        Assert.NotNull(restored);
        Assert.Equal(ProjectOverviewDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal("Prepared the sample", Assert.Single(restored.Projects).CurrentSummary);
        Assert.Equal("turn-1", Assert.Single(restored.Coverage).TurnId);
        Assert.Equal(1, Assert.Single(restored.Acknowledgements).Revision);
    }

    [Fact]
    public void Pruning_preserves_pinned_unresolved_and_live_updates_and_removes_orphan_acknowledgements()
    {
        var document = new ProjectOverviewDocument
        {
            Projects =
            [
                new ProjectDefinition { Id = "pinned", Name = "Pinned", IsPinned = true },
                new ProjectDefinition { Id = "ordinary", Name = "ordinary", CurrentSummary = "Old fictional summary", LastMeaningfulActivity = Now.AddDays(-100) },
            ],
            Memberships =
            [
                new ProjectMembership("pinned", "codex:pinned", ProjectMembershipSource.Manual),
                new ProjectMembership("ordinary", "codex:live", ProjectMembershipSource.Manual),
                new ProjectMembership("ordinary", "codex:question", ProjectMembershipSource.Manual),
                new ProjectMembership("ordinary", "codex:done", ProjectMembershipSource.Manual),
            ],
            Updates =
            [
                Update("old-pinned", "pinned", "codex:pinned", Now.AddDays(-100)),
                Update("old-live", "ordinary", "codex:live", Now.AddDays(-100)),
                Update("old-input", "ordinary", "codex:question", Now.AddDays(-100)) with { NeedsInput = true },
                Update("old-pruned", "ordinary", "codex:done", Now.AddDays(-100)),
            ],
            Acknowledgements =
            [
                new ProjectAcknowledgement("old-pinned", 1),
                new ProjectAcknowledgement("old-pruned", 1),
            ],
            Coverage = [new ProjectCoverageCheckpoint("ordinary", "codex:done", "turn-old", Now.AddDays(-100), "1", "p", "m")],
        };

        Assert.True(ProjectOverviewDocumentValidator.TryValidate(document, out var diagnostic), diagnostic);
        var result = ProjectOverviewDocumentRules.Prune(document, Now, new HashSet<string>(["codex:live"], StringComparer.Ordinal));

        Assert.Equal(new[] { "old-input", "old-live", "old-pinned" }, result.Updates.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("old-pinned", Assert.Single(result.Acknowledgements).UpdateId);
        Assert.Empty(result.Coverage);
        Assert.Equal("Old fictional summary", result.Projects.Single(project => project.Id == "ordinary").CurrentSummary);
    }

    [Fact]
    public void Deleting_project_purges_its_summaries_checkpoints_and_acknowledgements_but_keeps_shared_thread_updates()
    {
        var document = new ProjectOverviewDocument
        {
            Projects = [Project("p1"), Project("p2")],
            Memberships = [new ProjectMembership("p1", "codex:t", ProjectMembershipSource.Manual), new ProjectMembership("p2", "codex:t", ProjectMembershipSource.Manual)],
            Updates = [Update("u1", "p1", "codex:t", Now), Update("u2", "p2", "codex:t", Now)],
            Acknowledgements = [new ProjectAcknowledgement("u1", 1), new ProjectAcknowledgement("u2", 1)],
            Coverage =
            [
                new ProjectCoverageCheckpoint("p1", "codex:t", "turn-1", Now, "1", "p", "m"),
                new ProjectCoverageCheckpoint("p2", "codex:t", "turn-1", Now, "1", "p", "m"),
            ],
        };

        var result = ProjectOverviewDocumentRules.DeleteProject(document, "p1");

        Assert.Equal("p2", Assert.Single(result.Projects).Id);
        Assert.Equal("p2", Assert.Single(result.Memberships).ProjectId);
        Assert.Equal("u2", Assert.Single(result.Updates).Id);
        Assert.Equal("u2", Assert.Single(result.Acknowledgements).UpdateId);
        Assert.Equal("p2", Assert.Single(result.Coverage).ProjectId);
    }

    static ProjectDefinition Project(string id) => new() { Id = id, Name = id };

    static ProjectUpdate Update(string id, string projectId, string threadId, DateTimeOffset at) => new()
    {
        Id = id,
        ProjectId = projectId,
        ThreadId = threadId,
        Revision = 1,
        OccurredAt = at,
        LatestAction = "Changed a fictional example",
    };
}
