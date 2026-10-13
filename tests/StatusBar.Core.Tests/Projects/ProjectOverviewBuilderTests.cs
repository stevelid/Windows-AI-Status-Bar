using StatusBar.Core.Projects;
using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectOverviewBuilderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");

    [Fact]
    public void Manual_assignment_overrides_suggested_and_explicit_candidates()
    {
        var tasks = new[] { Task("codex:thread-a", AgentTaskStatus.Working) };
        var projects = new[] { Project("suggested"), Project("reference"), Project("manual") };
        var links = new[]
        {
            new ProjectMembership("suggested", tasks[0].Id, ProjectMembershipSource.Suggested),
            new ProjectMembership("reference", tasks[0].Id, ProjectMembershipSource.ExplicitReference),
            new ProjectMembership("manual", tasks[0].Id, ProjectMembershipSource.Manual),
        };

        var state = Build(projects, links, tasks);

        Assert.Empty(state.UngroupedConversations);
        Assert.Empty(state.Projects.Single(item => item.Project.Id == "suggested").Conversations);
        Assert.Empty(state.Projects.Single(item => item.Project.Id == "reference").Conversations);
        Assert.Equal("codex:thread-a", Assert.Single(state.Projects.Single(item => item.Project.Id == "manual").Conversations).Task.Id);
    }

    [Fact]
    public void Explicit_reference_is_preferred_to_model_suggestions()
    {
        var task = Task("claude:thread-a", AgentTaskStatus.Complete);
        var state = Build(
            [Project("suggested"), Project("reference")],
            [
                new ProjectMembership("suggested", task.Id, ProjectMembershipSource.Suggested),
                new ProjectMembership("reference", task.Id, ProjectMembershipSource.ExplicitReference),
            ],
            [task]);

        Assert.Empty(state.Projects.Single(item => item.Project.Id == "suggested").Conversations);
        Assert.Single(state.Projects.Single(item => item.Project.Id == "reference").Conversations);
    }

    [Fact]
    public void Manual_ungroup_override_suppresses_suggestions_and_leaves_conversation_ungrouped()
    {
        var task = Task("codex:thread-a", AgentTaskStatus.Complete);
        var state = Build([Project("suggested")],
            [new ProjectMembership("suggested", task.Id, ProjectMembershipSource.Suggested),
             new ProjectMembership(string.Empty, task.Id, ProjectMembershipSource.Manual, IsUnassigned: true)], [task]);

        Assert.Single(state.UngroupedConversations);
        Assert.Empty(state.Projects.Single().Conversations);
    }

    [Fact]
    public void Cached_updates_recover_title_provider_and_trusted_session_reference_for_navigation()
    {
        var taskId = "claude:sample-session-id";
        var state = ProjectOverviewBuilder.Build([Project("project")],
            [new ProjectMembership("project", taskId, ProjectMembershipSource.Manual)], [],
            [Update("cached-update", "project", taskId, 1, "Prepared a fictional result")], [],
            [new ProjectThreadReference(taskId, AgentProvider.Claude, "Sample task", Now)]);

        var task = Assert.Single(Assert.Single(state.Projects).Conversations).Task;
        Assert.Equal("Sample task", task.Title);
        Assert.Equal(AgentProvider.Claude, task.Provider);
        Assert.Equal("sample-session-id", task.SessionReference);
        Assert.Equal(AgentTaskStatus.Unknown, task.Status);
    }

    [Fact]
    public void One_conversation_can_be_linked_to_multiple_projects_with_update_attribution_kept_separate()
    {
        var task = Task("claude:thread-a", AgentTaskStatus.Working);
        var updates = new[]
        {
            Update("update-a", "project-a", task.Id, 1, "Prepared the first draft"),
            Update("update-b", "project-b", task.Id, 1, "Checked the second calculation"),
        };
        var state = Build(
            [Project("project-a"), Project("project-b")],
            [
                new ProjectMembership("project-a", task.Id, ProjectMembershipSource.Manual),
                new ProjectMembership("project-b", task.Id, ProjectMembershipSource.Manual),
            ],
            [task],
            updates);

        Assert.All(state.Projects, item => Assert.Single(item.Conversations));
        Assert.Equal("update-a", Assert.Single(state.Projects.Single(item => item.Project.Id == "project-a").Updates).Id);
        Assert.Equal("update-b", Assert.Single(state.Projects.Single(item => item.Project.Id == "project-b").Updates).Id);
    }

    [Fact]
    public void Working_and_needs_input_can_both_be_true_and_input_stays_prominent()
    {
        var active = Task("codex:active", AgentTaskStatus.Working);
        var needsInput = Update("input", "project-active", active.Id, 1, "Asked which option to use") with { NeedsInput = true };
        var ordinary = Task("codex:ordinary", AgentTaskStatus.Working);
        var state = Build(
            [Project("project-ordinary"), Project("project-active")],
            [
                new ProjectMembership("project-active", active.Id, ProjectMembershipSource.Manual),
                new ProjectMembership("project-ordinary", ordinary.Id, ProjectMembershipSource.Manual),
            ],
            [active, ordinary],
            [needsInput]);

        var first = state.Projects[0];
        Assert.Equal("project-active", first.Project.Id);
        Assert.True(first.HasWorkingConversation);
        Assert.True(first.HasUnresolvedInput);
        Assert.Equal(1, first.UnseenUpdateCount);
    }

    [Fact]
    public void Acknowledging_one_revision_does_not_acknowledge_a_newer_result_or_another_thread()
    {
        var firstThread = Task("claude:thread-a", AgentTaskStatus.Complete);
        var secondThread = Task("codex:thread-b", AgentTaskStatus.Complete);
        var original = Update("turn-a", "project", firstThread.Id, 1, "Drafted the report");
        var newer = original with { Revision = 2, LatestAction = "Revised the report" };
        var otherThread = Update("turn-b", "project", secondThread.Id, 1, "Checked a chart");
        var state = Build(
            [Project("project")],
            [
                new ProjectMembership("project", firstThread.Id, ProjectMembershipSource.Manual),
                new ProjectMembership("project", secondThread.Id, ProjectMembershipSource.Manual),
            ],
            [firstThread, secondThread],
            [newer, otherThread],
            [new ProjectAcknowledgement(original.Id, original.Revision)]);

        var item = Assert.Single(state.Projects);
        Assert.Equal(2, item.UnseenUpdateCount);
        Assert.Equal(0, item.OverflowUpdateCount);
    }

    [Fact]
    public void More_than_two_updates_remain_in_state_and_are_reported_as_overflow()
    {
        var tasks = Enumerable.Range(0, 3).Select(index => Task($"codex:thread-{index}", AgentTaskStatus.Complete)).ToArray();
        var updates = tasks.Select((task, index) => Update($"update-{index}", "project", task.Id, 1, $"Completed step {index}")).ToArray();
        var links = tasks.Select(task => new ProjectMembership("project", task.Id, ProjectMembershipSource.Manual));

        var item = Assert.Single(Build([Project("project")], links, tasks, updates).Projects);

        Assert.Equal(3, item.Updates.Count);
        Assert.Equal(1, item.OverflowUpdateCount);
    }

    [Fact]
    public void Invalid_or_unassociated_model_updates_are_not_projected()
    {
        var task = Task("codex:thread", AgentTaskStatus.Complete);
        var oversized = Update("oversized", "project", task.Id, 1, new string('x', ProjectTextLimits.MaximumActionLength + 1));
        var wrongThread = Update("wrong-thread", "project", "codex:other", 1, "Made a change");

        var item = Assert.Single(Build(
            [Project("project")],
            [new ProjectMembership("project", task.Id, ProjectMembershipSource.Manual)],
            [task],
            [oversized, wrongThread]).Projects);

        Assert.Empty(item.Updates);
        Assert.Equal(0, item.UnseenUpdateCount);
    }

    static ProjectOverviewState Build(
        IEnumerable<ProjectDefinition> projects,
        IEnumerable<ProjectMembership> memberships,
        IEnumerable<AgentTask> tasks,
        IEnumerable<ProjectUpdate>? updates = null,
        IEnumerable<ProjectAcknowledgement>? acknowledgements = null) =>
        ProjectOverviewBuilder.Build(projects, memberships, tasks, updates ?? [], acknowledgements ?? []);

    static ProjectDefinition Project(string id) => new() { Id = id, Name = id };

    static AgentTask Task(string id, AgentTaskStatus status) => new()
    {
        Id = id,
        Provider = id.StartsWith("claude:", StringComparison.Ordinal) ? AgentProvider.Claude : AgentProvider.Codex,
        Title = "Made-up conversation",
        Status = status,
        Confidence = StateConfidence.Confirmed,
        LastActivity = Now,
    };

    static ProjectUpdate Update(string id, string projectId, string threadId, int revision, string action) => new()
    {
        Id = id,
        ProjectId = projectId,
        ThreadId = threadId,
        Revision = revision,
        OccurredAt = Now,
        LatestAction = action,
        Progress = ["Made a fictional change"],
        SourceEvidenceIds = ["evidence-1"],
    };
}
