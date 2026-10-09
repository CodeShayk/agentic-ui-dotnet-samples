using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// Streams captured from Ch07.Beacon.Agent, which restates the conversation with MESSAGES_SNAPSHOT at
/// the end of every run. Two turns replayed back to back are Beacon Desk's first minute.
/// </summary>
public sealed class AgentRecordingTests
{
    [Fact]
    public async Task Two_turns_reconcile_cleanly_and_the_second_sends_the_first()
    {
        var transport = new ReplayTransport(
            await Recordings.LoadAsync("agent-wo-1001-status.sse"),
            await Recordings.LoadAsync("agent-diagnose.sse"));
        using var harness = new Harness(transport);
        var ct = TestContext.Current.CancellationToken;

        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }, cancellationToken: ct);

        // Turn 1: the snapshot restated exactly what the client already had.
        var view = harness.View;
        Assert.Equal(["user-1", "msg-0001", "call-0001-1", "msg-0002"], view.Messages.Select(m => m.Id));
        Assert.Equal(new ReconciliationSummary(Replaced: 4, Changed: 0, Added: 0, Dropped: 0, Kept: 0), view.LastReconciliation);
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("get_work_order", ToolCallStatus.Completed, "msg-0001"), (call.Name, call.Status, call.ParentMessageId));
        Assert.StartsWith("**WO-1001** is Scheduled", harness.Message<AssistantMessageView>("msg-0002").Text);

        await harness.Conversation.SendAsync(new ChatMessage(ChatRole.User, "Why is it short-cycling?") { MessageId = "user-2" }, cancellationToken: ct);

        // Turn 2 sent the whole conversation so far, on the same thread, as AG-UI messages.
        var second = transport.Inputs[1];
        Assert.Equal(("t-1", "1.0"), (second.ThreadId, second.ProtocolVersion));
        Assert.Equal(
            [("user", "user-1"), ("assistant", "msg-0001"), ("tool", "call-0001-1"), ("assistant", "msg-0002"), ("user", "user-2")],
            second.Messages.Select(m => (m.Role, m.Id)));
        var restatedCall = Assert.Single(Assert.IsType<AGUIAssistantMessage>(second.Messages[1]).ToolCalls!);
        Assert.Equal(("get_work_order", "{\"workOrderId\":\"WO-1001\"}"), (restatedCall.Function.Name, restatedCall.Function.Arguments));
        Assert.Null(second.State); // no shared state yet, so none is sent

        // The agent's snapshot doesn't track reasoning, so the client keeps its reasoning message.
        view = harness.View;
        Assert.Equal(new ReconciliationSummary(Replaced: 6, Changed: 0, Added: 0, Dropped: 0, Kept: 1), view.LastReconciliation);
        Assert.IsType<ReasoningMessageView>(view.Messages[5]);
        Assert.StartsWith("The most likely cause", harness.Message<AssistantMessageView>("msg-0003").Text);
        Assert.Equal(["r-1", "r-2"], view.Runs.Select(r => r.RunId));
    }

    [Fact]
    public async Task A_stream_that_ends_before_its_run_finishes_is_a_failure()
    {
        // Captured with curl --max-time 1 while get_schedule was still running.
        using var harness = await Harness.ReplayAsync("agent-truncated.sse", threadId: "t-2");
        var view = harness.View;

        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal(FailureKind.StreamEnded, view.Failure!.Kind);
        Assert.Equal(RunOutcome.Failed, view.CurrentRun!.Outcome);

        // The tap saw the call start; it never finished, so it isn't sent back next turn.
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("get_schedule", ToolCallStatus.Abandoned), (call.Name, call.Status));
        Assert.DoesNotContain(ConversationHistory.ToChatMessages(view), m => m.Contents.OfType<FunctionCallContent>().Any());
    }
}
