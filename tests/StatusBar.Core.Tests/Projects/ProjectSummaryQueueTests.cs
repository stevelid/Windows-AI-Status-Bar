using Microsoft.Extensions.Time.Testing;
using StatusBar.Core.Projects;

namespace StatusBar.Core.Tests.Projects;

public sealed class ProjectSummaryQueueTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-06-17T12:00:00Z");

    [Fact]
    public void Coalesces_same_thread_turns_and_marks_dequeued_result_stale_after_new_evidence()
    {
        var queue = new ProjectSummaryQueue(new FakeTimeProvider(Now));
        Assert.True(queue.Enqueue(Request("turn-1", "first action", Now.AddMinutes(-1))));
        Assert.True(queue.Enqueue(Request("turn-2", "second action", Now)));
        Assert.True(queue.TryDequeue(out var item));
        Assert.Equal("turn-2", item.Request.EvidenceId);
        Assert.Equal("turn-1", Assert.Single(item.Request.EarlierEvidence!).EvidenceId);
        Assert.True(queue.IsCurrent(item));
        Assert.False(queue.Enqueue(Request("turn-2", "duplicate event", Now)));

        Assert.True(queue.Enqueue(Request("turn-3", "newer action", Now.AddMinutes(1))));
        Assert.False(queue.IsCurrent(item));
        queue.Complete(item);
        Assert.False(queue.Enqueue(Request("turn-3", "duplicate pending event", Now.AddMinutes(1))));
    }

    [Fact]
    public void New_turn_during_worker_flight_carries_in_flight_evidence_forward()
    {
        var queue = new ProjectSummaryQueue(new FakeTimeProvider(Now));
        Assert.True(queue.Enqueue(Request("turn-a", "first", Now)));
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.Enqueue(Request("turn-b", "second", Now.AddMinutes(1))));
        Assert.False(queue.IsCurrent(first));
        Assert.True(queue.TryDequeue(out var next));
        Assert.Equal("turn-a", Assert.Single(next.Request.EarlierEvidence!).EvidenceId);
        Assert.True(queue.IsCurrent(next));
    }

    [Fact]
    public void Coalesced_turns_stay_under_utf8_budget_and_mark_omitted_evidence_partial()
    {
        var queue = new ProjectSummaryQueue(new FakeTimeProvider(Now));
        for (var i = 0; i < 8; i++)
        {
            var request = Request("turn-" + i, new string('x', 1800), Now.AddMinutes(i));
            Assert.True(queue.Enqueue(request));
        }
        Assert.True(queue.TryDequeue(out var work));
        Assert.True(ProjectSummaryValidator.IsWithinInputBudget(work.Request));
        Assert.True(work.Request.PartialCoverage);
        Assert.True((work.Request.EarlierEvidence?.Count ?? 0) < 7);
    }

    [Fact]
    public void Launch_budget_uses_time_provider_and_expires_after_one_hour()
    {
        var time = new FakeTimeProvider(Now);
        var queue = new ProjectSummaryQueue(time);
        for (var i = 0; i < 12; i++) queue.RecordLaunch();
        Assert.True(queue.GetLaunchDelay() > TimeSpan.Zero);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.Zero, queue.GetLaunchDelay());
    }

    [Fact]
    public void Reducer_applies_no_op_checkpoint_without_inventing_project_and_uses_source_time()
    {
        var time = new FakeTimeProvider(Now.AddDays(5));
        var request = Request("turn-noop", "visible final", Now.AddDays(-1));
        var document = ProjectSummaryReducer.Apply(new ProjectOverviewDocument(), request,
            new ProjectSummaryResult(null, "", [], []), time);

        Assert.Empty(document.Projects);
        Assert.Null(Assert.Single(document.Coverage).ProjectId);
        Assert.Equal(request.OccurredAt, document.Coverage[0].CoveredAt);
        Assert.True(ProjectOverviewDocumentValidator.TryValidate(document, out var error), error);
    }

    [Fact]
    public void Manual_assignment_wins_but_existing_project_history_is_retained()
    {
        var original = new ProjectOverviewDocument
        {
            Projects = [new ProjectDefinition { Id = "p-old", Name = "Old project" }, new ProjectDefinition { Id = "p-manual", Name = "Chosen project" }],
            Memberships = [new("p-old", "codex:thread", ProjectMembershipSource.Suggested), new("p-manual", "codex:thread", ProjectMembershipSource.Manual)],
            Updates = [new ProjectUpdate { Id = "old-update", ProjectId = "p-old", ThreadId = "codex:thread", Revision = 1,
                OccurredAt = Now.AddDays(-2), LatestAction = "Prior fictional result", SourceEvidenceIds = ["old:assistant"] }],
        };
        var request = Request("turn-current", "new fictional result", Now);
        var result = new ProjectSummaryResult("Old project", "Updated chosen project", [], ["turn-current:assistant"]);

        var document = ProjectSummaryReducer.Apply(original, request, result, new FakeTimeProvider(Now.AddDays(2)));

        Assert.Contains(document.Updates, update => update.Id == "old-update");
        Assert.Contains(document.Updates, update => update.ProjectId == "p-manual" && update.LatestAction == "Updated chosen project");
        Assert.DoesNotContain(document.Projects, project => project.Name == "Old project" && project.CurrentSummary == "Updated chosen project");
        Assert.True(ProjectOverviewDocumentValidator.TryValidate(document, out var error), error);
    }

    [Fact]
    public void Reducer_replay_is_idempotent_and_keeps_acknowledgement()
    {
        var time = new FakeTimeProvider(Now);
        var request = Request("turn-idempotent", "new result", Now);
        var result = new ProjectSummaryResult("Fictional project", "Prepared a new result", [], ["turn-idempotent:assistant"]);
        var first = ProjectSummaryReducer.Apply(new ProjectOverviewDocument(), request, result, time);
        var update = Assert.Single(first.Updates);
        first = first with { Acknowledgements = [new ProjectAcknowledgement(update.Id, update.Revision)] };

        var replay = ProjectSummaryReducer.Apply(first, request, result, time);

        Assert.Same(first, replay);
        Assert.Single(replay.Updates);
        Assert.Single(replay.Acknowledgements);
    }

    static ProjectSummaryRequest Request(string id, string assistant, DateTimeOffset occurredAt) => new(
        "codex:thread", id, "Fictional task", "fictional request", assistant, [], OccurredAt: occurredAt, EarlierEvidence: []);
}
