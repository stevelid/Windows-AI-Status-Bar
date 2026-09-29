using StatusBar.Core.Tasks;

namespace StatusBar.Core.Tests.Tasks;

public sealed class TaskNavigationTests
{
    const string Session = "00000000-0000-4000-8000-000000000001";

    [Fact]
    public void Links_target_the_selected_provider_and_session()
    {
        var task = Task(AgentProvider.Codex, Session);
        Assert.Equal("codex://threads/" + Session, TaskNavigation.CreateLink(task)!.AbsoluteUri);
        task = task with { Provider = AgentProvider.Claude };
        Assert.Equal("claude://code/continue?session=local_" + Session,
            TaskNavigation.CreateLink(task, "local_" + Session)!.AbsoluteUri);
        Assert.Equal("claude://resume/?session=" + Session, TaskNavigation.CreateLink(task)!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("new")]
    [InlineData("../settings")]
    [InlineData("codex://threads/new")]
    [InlineData("00000000-0000-4000-8000-000000000001?prompt=hello")]
    public void Unusable_session_ids_do_not_launch_new_conversations_or_accept_urls(string? session)
    {
        Assert.Null(TaskNavigation.CreateLink(Task(AgentProvider.Codex, session)));
        Assert.Null(TaskNavigation.CreateLink(Task(AgentProvider.Claude, session)));
    }

    [Theory]
    [InlineData("last")]
    [InlineData("local_")]
    [InlineData("local_sample?session=last")]
    [InlineData("local_sample/other")]
    public void Invalid_desktop_ids_fall_back_to_the_exact_cli_session(string desktop)
    {
        Assert.Equal("claude://resume/?session=" + Session,
            TaskNavigation.CreateLink(Task(AgentProvider.Claude, Session), desktop)!.AbsoluteUri);
    }

    static AgentTask Task(AgentProvider provider, string? session) => new()
    {
        Id = "synthetic-task", Provider = provider, Title = "Sample task",
        Status = AgentTaskStatus.Working, Confidence = StateConfidence.Confirmed,
        LastActivity = DateTimeOffset.MinValue, SessionReference = session,
    };
}
