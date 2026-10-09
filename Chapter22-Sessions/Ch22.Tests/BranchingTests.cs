using System.Net;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Beacon.Testing;
using Ch22.Beacon.Desk.Sessions;
using Ch22.Tests.Infrastructure;

namespace Ch22.Tests;

/// <summary>"Regenerate from here": a new run that continues from the parent of the answer it replaces.</summary>
public sealed class BranchingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Regenerating_an_answer_branches_from_its_parent_run_and_the_snapshot_replaces_it()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-1005");
        await Elena.AskAsync(desk);
        await Elena.AnswerAsync(desk);
        await Elena.AskForNoteAsync(desk);
        await desk.Sessions.RefreshAsync(Ct);

        var first = desk.View.Messages.OfType<AssistantMessageView>().Last();
        var noteRun = desk.Sessions.RunThatWrote(first.Id)!;
        Assert.True(desk.Sessions.CanRegenerate(first.Id, desk.View));

        await desk.Sessions.RegenerateAsync(first.Id, Ct);

        // The regenerated run continues from the run before the note, and says so on RUN_STARTED.
        var regenerated = desk.View.CurrentRun!;
        Assert.Equal(noteRun.ParentRunId, regenerated.ParentRunId);

        // The agent restated the branch it continues; the reducer dropped the first note, which isn't on it.
        Assert.Equal(1, desk.View.LastReconciliation!.Dropped);
        Assert.Null(desk.View.FindMessage(first.Id));
        var second = desk.View.Messages.OfType<AssistantMessageView>().Last();
        Assert.NotEqual(first.Text, second.Text);
        Assert.Single(desk.View.Messages.OfType<UserMessageView>(), m => m.Text == Elena.NoteQuestion);

        // Two branches now share everything up to the question.
        var document = desk.Sessions.Document!;
        Assert.Equal(2, document.Leaves.Count);
        Assert.Equal(regenerated.RunId, document.HeadRunId);
        Assert.Equal(new AnswerVersions(2, 2, noteRun.RunId, null), desk.Sessions.VersionsOf(second.Id, desk.View));

        // The model answered the same question on the same history, without the first note.
        var call = agent.Model.RunCalls[^1].Messages;
        Assert.Equal(Elena.NoteQuestion, call[^1].Text);
        Assert.DoesNotContain(call, m => m.Text == first.Text);
    }

    [Fact]
    public async Task Opening_the_other_version_restores_its_branch()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-1005");
        await Elena.AskAsync(desk);
        await Elena.AnswerAsync(desk);
        await Elena.AskForNoteAsync(desk);
        var firstView = Rendered.Of(desk.View);
        await desk.Sessions.RefreshAsync(Ct);
        var first = desk.View.Messages.OfType<AssistantMessageView>().Last();
        await desk.Sessions.RegenerateAsync(first.Id, Ct);

        var versions = desk.Sessions.VersionsOf(desk.View.Messages.OfType<AssistantMessageView>().Last().Id, desk.View)!;
        await desk.Sessions.OpenAsync("t-1005", versions.Previous, Ct);

        Assert.Equal(firstView, Rendered.Of(desk.View));
    }

    [Fact]
    public async Task The_first_answer_is_regenerated_from_the_start_of_the_thread()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-start");
        await desk.Conversation.SendAsync("Draft a note for the facilities manager at Northgate about WO-1005.", cancellationToken: Ct);
        await desk.Sessions.RefreshAsync(Ct);
        var first = desk.View.Messages.OfType<AssistantMessageView>().Last();

        await desk.Sessions.RegenerateAsync(first.Id, Ct);

        // No run came before it, so the new run has no parent: a second root, beside the first.
        Assert.Null(desk.View.CurrentRun!.ParentRunId);
        Assert.Equal(2, desk.Sessions.Document!.Runs.Count(r => r.ParentRunId is null));
        Assert.NotEqual(first.Text, desk.View.Messages.OfType<AssistantMessageView>().Last().Text);
        Assert.Single(desk.View.Messages.OfType<UserMessageView>());
    }

    [Fact]
    public async Task A_parent_run_the_thread_doesnt_have_is_refused_before_the_stream()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var http = agent.CreateClient();
        await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun("What is the status of WO-1005?", "t-x", "r-1"), Ct);

        var input = new RunAgentInput { ThreadId = "t-x", RunId = "r-2", ParentRunId = "r-9", Messages = [Wire.User("user-2", "And WO-1001?")] };
        using var response = await Wire.PostAsync(http, input, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Unknown parent run", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
