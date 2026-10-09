using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch26.Tests.Unit.Reducer;

/// <summary>
/// The cheapest tests in the pyramid. ConversationReducer.Reduce is a pure function (Chapter 7), so each
/// test is a list of actions and an assertion about the view they fold into: no client, no transport,
/// no timing. They run in milliseconds and pin down what every Beacon UI renders from.
/// </summary>
public sealed class ConversationReducerTests
{
    [Fact]
    public void A_state_snapshot_replaces_the_shared_state()
    {
        var view = Fold(
            new ConversationReset("thread-1"),
            new TurnStarted(),
            Received(new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" }),
            Received(new StateSnapshotEvent { Snapshot = Json("""{"activeWorkOrder":{"id":"WO-1001","status":"Scheduled"}}""") }),
            Received(new RunFinishedEvent { ThreadId = "thread-1", RunId = "run-1", Outcome = new RunFinishedSuccessOutcome() }),
            new TurnEnded(TurnEnd.Completed));

        Assert.Equal("WO-1001", view.State.Document?["activeWorkOrder"]?["id"]?.GetValue<string>());
        Assert.Equal(StateChangeSource.Agent, view.State.LastChangedBy);
        Assert.Equal(ConversationStatus.Idle, view.Status);
    }

    [Fact]
    public void Text_deltas_build_one_assistant_message_that_stops_streaming_at_its_end()
    {
        var streaming = Fold(
            new ConversationReset("thread-1"),
            Received(new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" }),
            Received(new TextMessageContentEvent { MessageId = "msg-1", Delta = "**WO-1001** is " }, "**WO-1001** is "),
            Received(new TextMessageContentEvent { MessageId = "msg-1", Delta = "Scheduled." }, "Scheduled."));

        var message = Assert.IsType<AssistantMessageView>(Assert.Single(streaming.Messages));
        Assert.Equal("**WO-1001** is Scheduled.", message.Text);
        Assert.True(message.IsStreaming);

        var ended = ConversationReducer.Reduce(streaming, new WireEventObserved(new TextMessageEndEvent { MessageId = "msg-1" }));
        Assert.False(Assert.IsType<AssistantMessageView>(Assert.Single(ended.Messages)).IsStreaming);
    }

    [Fact]
    public void An_approval_interrupt_waits_for_an_answer_and_is_answered_once()
    {
        var waiting = Fold(
            new ConversationReset("thread-1"),
            new TurnStarted(),
            Received(new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" }),
            Received(new RunFinishedEvent
            {
                ThreadId = "thread-1",
                RunId = "run-1",
                Outcome = new RunFinishedInterruptOutcome
                {
                    Interrupts = [new AGUIInterrupt { Id = "apr_1", Reason = "tool_call", ToolCallId = "call-1" }],
                },
            }),
            new TurnEnded(TurnEnd.Completed));

        var interrupt = Assert.Single(waiting.OpenInterrupts);
        Assert.Equal(("apr_1", InterruptKind.ToolApproval), (interrupt.Id, interrupt.Kind));
        Assert.Equal(ConversationStatus.AwaitingInput, waiting.Status);

        var answered = ConversationReducer.Reduce(waiting, new InterruptsAnswered(["apr_1"]));
        Assert.Empty(answered.OpenInterrupts);
        Assert.Single(answered.Interrupts); // kept, marked answered: it can't be answered twice
    }

    [Fact]
    public void Reduce_never_changes_the_view_it_was_given()
    {
        var before = Fold(
            new ConversationReset("thread-1"),
            Received(new RunStartedEvent { ThreadId = "thread-1", RunId = "run-1" }),
            Received(new TextMessageContentEvent { MessageId = "msg-1", Delta = "Checking" }, "Checking"));

        var after = ConversationReducer.Reduce(before, Received(new TextMessageContentEvent { MessageId = "msg-1", Delta = " WO-1001." }, " WO-1001."));

        Assert.Equal("Checking", Assert.IsType<AssistantMessageView>(Assert.Single(before.Messages)).Text);
        Assert.Equal("Checking WO-1001.", Assert.IsType<AssistantMessageView>(Assert.Single(after.Messages)).Text);
        Assert.Same(before.ToolCalls, after.ToolCalls); // untouched slices are shared, so a UI can skip them
        Assert.Same(before.State, after.State);
    }

    private static ConversationView Fold(params ConversationAction[] actions) =>
        actions.Aggregate(ConversationView.Empty, ConversationReducer.Reduce);

    // What AGUIChatClient yields for an event: the event on RawRepresentation, plus any content it made of it.
    private static UpdateReceived Received(BaseEvent evt, string? text = null) =>
        new(new ChatResponseUpdate(ChatRole.Assistant, text is null ? [] : [new TextContent(text)])
        {
            RawRepresentation = evt,
            MessageId = (evt as TextMessageContentEvent)?.MessageId,
        });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
