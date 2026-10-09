using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch22.Beacon.Desk.Sessions;
using Ch22.Tests.Infrastructure;

namespace Ch22.Tests;

/// <summary>
/// The chapter's proof: a conversation restored from the agent's store, after the agent restarted, renders
/// exactly as the live one did, whether it's folded through the pure reducer or restored into a conversation.
/// </summary>
public sealed class RestoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_restored_conversation_renders_exactly_as_the_live_one_after_a_restart()
    {
        using var folder = new TempFolder();
        string live;

        // Live: Elena's question, the readings, a note for the customer.
        await using (var agent = new AgentHost(folder.Path))
        {
            using var desk = new DeskStack(agent, "t-1005");
            await Elena.AskAsync(desk);
            await Elena.AnswerAsync(desk);
            await Elena.AskForNoteAsync(desk);

            Assert.Equal(ConversationStatus.Idle, desk.View.Status);
            live = Rendered.Of(desk.View);
        }

        // The agent restarts. A fresh Desk opens the thread from the list.
        await using var restarted = new AgentHost(folder.Path);
        using var reopened = new DeskStack(restarted, "thread-unused");

        var listed = Assert.Single(await reopened.Threads.ListAsync("WO-1005", Ct));
        Assert.Equal("t-1005", listed.ThreadId);
        Assert.Equal(3, listed.RunCount);

        var events = await reopened.Threads.ReadEventsAsync("t-1005", runId: null, Ct);
        Assert.Equal(["RUN_STARTED", "MESSAGES_SNAPSHOT", "RUN_FINISHED"], events.Select(e => e.Type));

        // The same pure reducer, fed the stored branch...
        Assert.Equal(live, Rendered.Of(ConversationReplay.Fold("t-1005", events)));

        // ...and the Desk's restore into a live conversation, which the next turn continues.
        await reopened.Sessions.OpenAsync("t-1005", cancellationToken: Ct);
        Assert.Equal(live, Rendered.Of(reopened.View));
    }

    [Fact]
    public async Task An_open_question_comes_back_after_a_restart_and_can_be_answered()
    {
        using var folder = new TempFolder();
        string asked;

        await using (var agent = new AgentHost(folder.Path))
        {
            using var desk = new DeskStack(agent, "t-1005");
            await Elena.AskAsync(desk);
            Assert.Equal(ConversationStatus.AwaitingInput, desk.View.Status);
            asked = Rendered.Of(desk.View);
        }

        await using var restarted = new AgentHost(folder.Path);
        using var reopened = new DeskStack(restarted, "thread-unused");
        Assert.True(Assert.Single(await reopened.Threads.ListAsync("WO-1005", Ct)).AwaitingInput);

        await reopened.Sessions.OpenAsync("t-1005", cancellationToken: Ct);
        Assert.Equal(asked, Rendered.Of(reopened.View));

        var question = Assert.Single(reopened.View.OpenInterrupts);
        Assert.Equal(InterruptReasons.InputRequired, question.Reason);
        Assert.Equal(ConversationStatus.AwaitingInput, reopened.View.Status);

        // The question was restored from the agent's record, not the client's memory, and it answers like a live one.
        await Elena.AnswerAsync(reopened);

        Assert.Empty(reopened.View.OpenInterrupts);
        var verdict = reopened.View.Messages.OfType<AssistantMessageView>().Last().Text;
        Assert.Contains("4.3% unbalanced", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_text_the_client_was_shown_is_restored_but_never_sent_to_the_model()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path);
        using var desk = new DeskStack(agent, "t-why");
        await desk.Conversation.SendAsync("Why is WO-1001 short-cycling?", cancellationToken: Ct);
        await desk.Conversation.SendAsync("What is the status of WO-1001?", cancellationToken: Ct);

        var restored = ConversationReplay.Fold("t-why", await desk.Threads.ReadEventsAsync("t-why", null, Ct));

        Assert.Single(restored.Messages.OfType<ReasoningMessageView>());
        Assert.Equal(Rendered.Of(desk.View), Rendered.Of(restored));

        // Stored for the transcript, left out of the history: reasoning text never goes back to the model (only a
        // sealed value would, and this reasoning has none; see SealedReasoningTests).
        Assert.DoesNotContain(agent.Model.RunCalls[^1].Messages, m => m.Contents.OfType<Microsoft.Extensions.AI.TextReasoningContent>().Any());
    }

    [Fact]
    public async Task A_run_the_client_stopped_is_restored_with_what_was_shown()
    {
        using var folder = new TempFolder();
        await using var agent = new AgentHost(folder.Path, chunkDelay: TimeSpan.FromMilliseconds(30));
        using var desk = new DeskStack(agent, "t-stop");

        // Stop as soon as the first words arrive.
        desk.Conversation.Changed += (_, e) =>
        {
            if (e.View.Messages.OfType<AssistantMessageView>().Any(m => m.Text.Length > 0))
            {
                desk.Conversation.Stop();
            }
        };
        await desk.Conversation.SendAsync("Why is WO-1001 short-cycling?", cancellationToken: Ct);
        Assert.Equal(ConversationStatus.Stopped, desk.View.Status);

        // The server finishes recording the run when the stream unwinds. The client left, so the server stopped
        // the run on purpose: cancelled, not failed.
        var document = await WaitForAsync(() => desk.Threads.GetAsync("t-stop", Ct), d => d?.Runs.Count == 1);
        Assert.Equal("cancelled", Assert.Single(document!.Runs).Outcome);

        var restored = ConversationReplay.Fold("t-stop", await desk.Threads.ReadEventsAsync("t-stop", null, Ct));
        Assert.Equal(ConversationStatus.Cancelled, restored.Status);
        Assert.Equal(
            desk.View.Messages.OfType<UserMessageView>().Select(m => m.Text),
            restored.Messages.OfType<UserMessageView>().Select(m => m.Text));
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        for (var attempt = 0; ; attempt++)
        {
            var value = await read();
            if (done(value) || attempt == 50)
            {
                return value;
            }

            await Task.Delay(50, Ct);
        }
    }
}
