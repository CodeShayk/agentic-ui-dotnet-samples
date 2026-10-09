using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// The reducer on its own: a pure function, so these tests fold hand-written events and check the view.
/// MESSAGES_SNAPSHOT reconciliation and failed patches get the edge cases the recordings don't cover.
/// </summary>
public sealed class ReducerTests
{
    private static readonly ConversationView Start = ConversationReducer.Reduce(ConversationView.Empty, new ConversationReset("t-1"));

    [Fact]
    public void Reduce_never_changes_the_view_it_was_given()
    {
        var before = Fold(Start, Run("r-1"), Text("msg-1", "Hello"));
        var after = Fold(before, new TextMessageContentEvent { MessageId = "msg-1", Delta = ", Dana" });

        Assert.Equal("Hello", ((AssistantMessageView)before.FindMessage("msg-1")!).Text);
        Assert.Equal("Hello, Dana", ((AssistantMessageView)after.FindMessage("msg-1")!).Text);
        Assert.Same(before.ToolCalls, after.ToolCalls); // untouched slices are shared, not copied
    }

    [Fact]
    public void Snapshot_replaces_in_place_appends_the_unseen_and_drops_the_absent()
    {
        var view = Fold(Start, Run("r-1"), User("user-1", "What is the status of WO-1001?"), Text("msg-1", "WO-1001 is Scheduled."),
            User("user-2", "And WO-1003?"), Text("msg-2", "Checking WO-1003."));

        view = Fold(view, Snapshot(
            new AGUIUserMessage { Id = "user-1", Content = new AGUIContent("What is the status of WO-1001?") },
            new AGUIAssistantMessage { Id = "msg-1", Content = "WO-1001 is In progress: Priya arrived at 9:05." },
            new AGUIAssistantMessage { Id = "msg-0", Content = "Good morning, Dana." },
            new AGUIUserMessage { Id = "user-2", Content = new AGUIContent("And WO-1003?") }));

        // msg-1 changed in place; msg-2 is gone; msg-0, unseen, goes at the end even though it came first.
        Assert.Equal(["user-1", "msg-1", "user-2", "msg-0"], view.Messages.Select(m => m.Id));
        Assert.Equal("WO-1001 is In progress: Priya arrived at 9:05.", ((AssistantMessageView)view.Messages[1]).Text);
        Assert.Equal(new ReconciliationSummary(Replaced: 3, Changed: 1, Added: 1, Dropped: 1, Kept: 0), view.LastReconciliation);
    }

    [Fact]
    public void Snapshot_keeps_activity_and_reasoning_unless_it_carries_that_role()
    {
        var view = Fold(Start, Run("r-1"),
            new ActivitySnapshotEvent { MessageId = "act-1", ActivityType = "parts-search", Content = Json("""{"found":0}""") },
            new ReasoningMessageStartEvent { MessageId = "think-1", Role = AGUIRoles.Reasoning },
            new ReasoningMessageContentEvent { MessageId = "think-1", Delta = "Check the capacitor first." },
            new ReasoningMessageEndEvent { MessageId = "think-1" },
            Text("msg-1", "Measure CAP-45-5."));

        var withoutRoles = Fold(view, Snapshot(new AGUIAssistantMessage { Id = "msg-1", Content = "Measure CAP-45-5." }));
        Assert.Equal(["act-1", "think-1", "msg-1"], withoutRoles.Messages.Select(m => m.Id));
        Assert.Equal(2, withoutRoles.LastReconciliation!.Kept);

        // A snapshot that carries a reasoning message declares the complete reasoning set.
        var withReasoning = Fold(view, Snapshot(
            new AGUIReasoningMessage { Id = "think-2", Content = "Restated reasoning." },
            new AGUIAssistantMessage { Id = "msg-1", Content = "Measure CAP-45-5." }));
        Assert.Equal(["act-1", "msg-1", "think-2"], withReasoning.Messages.Select(m => m.Id));
    }

    [Fact]
    public void Snapshot_restating_a_stopped_answer_keeps_the_partial_note()
    {
        // Stopped mid-message: no TEXT_MESSAGE_END arrived.
        var view = Fold(Start, Run("r-1"),
            new TextMessageStartEvent { MessageId = "msg-1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg-1", Delta = "Drafting the report: arrived 9:05," });
        view = ConversationReducer.Reduce(view, new TurnEnded(TurnEnd.Stopped));
        Assert.True(((AssistantMessageView)view.FindMessage("msg-1")!).IsPartial);

        view = Fold(view, Run("r-2"), Snapshot(new AGUIAssistantMessage { Id = "msg-1", Content = "Drafting the report: arrived 9:05," }));

        Assert.True(((AssistantMessageView)view.FindMessage("msg-1")!).IsPartial);
    }

    [Fact]
    public void Snapshot_takes_tool_calls_with_their_messages()
    {
        var view = Fold(Start, Run("r-1"),
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "get_work_order", ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = "{\"workOrderId\":\"WO-1001\"}" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new ToolCallResultEvent { MessageId = "res-1", ToolCallId = "call-1", Content = new AGUIContent("{\"status\":\"Scheduled\"}") });

        // The producer restates a different conversation that no longer has that call.
        view = Fold(view, Snapshot(
            new AGUIAssistantMessage
            {
                Id = "msg-9",
                ToolCalls = [new AGUIToolCall { Id = "call-9", Type = "function", Function = new AGUIToolCallFunction { Name = "check_warranty", Arguments = "{\"equipmentId\":\"EQ-501\"}" } }],
            },
            new AGUIToolMessage { Id = "res-9", ToolCallId = "call-9", Content = new AGUIContent("{\"covered\":true}") }));

        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("call-9", "check_warranty", ToolCallStatus.Completed, "{\"covered\":true}"), (call.Id, call.Name, call.Status, call.Result));
        Assert.Equal(["msg-9", "res-9"], view.Messages.Select(m => m.Id));
    }

    [Fact]
    public void A_delta_that_does_not_apply_keeps_the_state_and_asks_for_a_resync()
    {
        var view = Fold(Start, Run("r-1"),
            new StateSnapshotEvent { Snapshot = Json("""{"checklist":[{"id":"C1","done":false}]}""") },
            new StateDeltaEvent { Delta = Json("""[{"op":"replace","path":"/checklist/0/done","value":true},{"op":"remove","path":"/checklist/5"}]""") });

        Assert.True(view.State.NeedsResync);
        Assert.Equal(1, view.State.Version); // the failed delta didn't count as a change
        Assert.False(view.State.Document!["checklist"]![0]!["done"]!.GetValue<bool>()); // atomic: the first op didn't stick either
        Assert.Contains(view.Warnings, w => w.Contains("STATE_DELTA", StringComparison.Ordinal));

        // The next snapshot is adopted whatever was skipped before it.
        view = Fold(view, new StateSnapshotEvent { Snapshot = Json("""{"checklist":[{"id":"C1","done":true}]}""") });
        Assert.False(view.State.NeedsResync);
        Assert.True(view.State.Document!["checklist"]![0]!["done"]!.GetValue<bool>());
    }

    [Fact]
    public void A_delta_with_no_state_patches_an_empty_object()
    {
        var view = Fold(Start, Run("r-1"), new StateDeltaEvent { Delta = Json("""[{"op":"add","path":"/activeWorkOrder","value":"WO-1001"}]""") });

        Assert.Equal("""{"activeWorkOrder":"WO-1001"}""", view.State.Document!.ToJsonString());
    }

    [Fact]
    public void Activity_deltas_for_unknown_messages_are_skipped_not_fatal()
    {
        var view = Fold(Start, Run("r-1"),
            new ActivityDeltaEvent { MessageId = "act-404", ActivityType = "parts-search", Patch = Json("""[{"op":"replace","path":"/found","value":1}]""") },
            Text("msg-1", "Still here."));

        Assert.Equal(["msg-1"], view.Messages.Select(m => m.Id));
        Assert.Contains(view.Warnings, w => w.Contains("act-404", StringComparison.Ordinal));
    }

    [Fact]
    public void Lone_run_error_before_any_run_still_fails_the_turn()
    {
        // What an unreachable model produces: RUN_ERROR and nothing else.
        var view = Fold(ConversationReducer.Reduce(Start, new TurnStarted()), new RunErrorEvent { Message = "The model is unavailable." });
        view = ConversationReducer.Reduce(view, new TurnEnded(TurnEnd.Completed));

        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal("The model is unavailable.", view.Failure!.Message);
        Assert.Empty(view.Runs);
    }

    [Fact]
    public void History_sends_completed_calls_and_text_but_never_activity_reasoning_or_unfinished_calls()
    {
        var view = Fold(Start, Run("r-1"), User("user-1", "Look up WO-1001"),
            new ReasoningMessageStartEvent { MessageId = "think-1", Role = AGUIRoles.Reasoning },
            new ReasoningMessageContentEvent { MessageId = "think-1", Delta = "Need the work order." },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "get_work_order", ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = "{\"workOrderId\":\"WO-1001\"}" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new ToolCallResultEvent { MessageId = "res-1", ToolCallId = "call-1", Content = new AGUIContent("{\"status\":\"Scheduled\"}") },
            new ActivitySnapshotEvent { MessageId = "act-1", ActivityType = "lookup", Content = Json("""{"done":true}""") },
            new ToolCallStartEvent { ToolCallId = "call-2", ToolCallName = "get_schedule", ParentMessageId = "msg-2" },
            new ToolCallArgsEvent { ToolCallId = "call-2", Delta = "{\"technicianId\":" });
        view = ConversationReducer.Reduce(view, new TurnEnded(TurnEnd.Stopped));

        var history = ConversationHistory.ToChatMessages(view);

        Assert.Equal(["user-1", "msg-1", "res-1"], history.Select(m => m.MessageId));
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(history[1].Contents));
        Assert.Equal(("call-1", "get_work_order", "WO-1001"), (call.CallId, call.Name, ((JsonElement)call.Arguments!["workOrderId"]!).GetString()));
        Assert.Equal(ToolCallStatus.Abandoned, view.FindToolCall("call-2")!.Status);
    }

    [Fact]
    public void A_malformed_delta_fails_the_run_and_nothing_after_it_applies()
    {
        var view = Fold(Start, Run("r-1"),
            new StateSnapshotEvent { Snapshot = Json("""{"checklist":[]}""") },
            new StateDeltaEvent { Delta = Json("""[{"op":"add","value":"C1"}]""") }, // no path: malformed, not "doesn't apply"
            Text("msg-1", "Ignored."));

        Assert.Equal((FailureKind.ProtocolViolation, RunOutcome.Failed), (view.Failure!.Kind, view.CurrentRun!.Outcome));
        Assert.False(view.State.NeedsResync); // a violation isn't a divergence to resynchronize
        Assert.Null(view.FindMessage("msg-1"));

        view = ConversationReducer.Reduce(view, new TurnEnded(TurnEnd.Completed));
        Assert.Equal(ConversationStatus.Failed, view.Status);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("tool")]
    [InlineData("reasoning")]
    public void A_first_chunk_without_its_id_is_a_protocol_violation(string kind)
    {
        BaseEvent chunk = kind switch
        {
            "text" => new TextMessageChunkEvent { Delta = "Orphan text." },
            "tool" => new ToolCallChunkEvent { ToolCallName = "get_work_order", Delta = "{}" },
            _ => new ReasoningMessageChunkEvent { Delta = "Orphan thought." },
        };

        var view = Fold(Start, Run("r-1"), chunk);

        Assert.Equal(FailureKind.ProtocolViolation, view.Failure!.Kind);
        Assert.Empty(view.Messages); // the client never invents an ID
    }

    [Fact]
    public void A_first_tool_chunk_without_a_name_and_a_contradicting_repeat_are_violations()
    {
        var unnamed = Fold(Start, Run("r-1"), new ToolCallChunkEvent { ToolCallId = "call-1", Delta = "{" });
        Assert.Contains("toolCallName", unnamed.Failure!.Message, StringComparison.Ordinal);

        var contradicted = Fold(Start, Run("r-1"),
            new TextMessageChunkEvent { MessageId = "msg-1", Delta = "Opened without a role, so assistant. " },
            new TextMessageChunkEvent { Role = AGUIRoles.User, Delta = "Now claiming to be the user." });
        Assert.Contains("role 'user'", contradicted.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal("Opened without a role, so assistant. ", ((AssistantMessageView)contradicted.FindMessage("msg-1")!).Text);
    }

    [Fact]
    public void An_untagged_continuation_finds_the_only_open_stream_and_refuses_to_guess_between_two()
    {
        var one = Fold(Start, Run("r-1"),
            new TextMessageChunkEvent { MessageId = "msg-p", SubagentRunId = "parts-1", Delta = "Parts: " },
            new TextMessageChunkEvent { Delta = "CAP-45-5 in stock." });
        Assert.Equal("Parts: CAP-45-5 in stock.", ((AssistantMessageView)one.FindMessage("msg-p")!).Text);
        Assert.Null(one.Failure);

        var two = Fold(Start, Run("r-1"),
            new TextMessageChunkEvent { MessageId = "msg-p", SubagentRunId = "parts-1", Delta = "Parts: " },
            new TextMessageChunkEvent { MessageId = "msg-w", SubagentRunId = "warranty-1", Delta = "Warranty: " },
            new TextMessageChunkEvent { Delta = "whose is this?" });
        Assert.Equal(FailureKind.ProtocolViolation, two.Failure!.Kind);
    }

    [Fact]
    public void A_suspended_subagent_resumes_when_its_answer_is_accepted_and_continues_under_its_id()
    {
        var view = Fold(Start, Run("r-1"),
            new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" },
            new SubagentFinishedEvent { SubagentRunId = "parts-1", Outcome = new SubagentFinishedSuspendedOutcome { InterruptIds = ["apr-1"] } },
            new RunFinishedEvent
            {
                ThreadId = "t-1",
                RunId = "r-1",
                Outcome = new RunFinishedInterruptOutcome { Interrupts = [new AGUIInterrupt { Id = "apr-1", Reason = "input_required", SubagentRunId = "parts-1" }] },
            });
        Assert.Equal(SubagentStatus.Suspended, Assert.Single(view.Subagents).Status);

        // The agent accepted the answer (the resuming run started): no longer waiting.
        view = ConversationReducer.Reduce(view, new InterruptsAnswered(["apr-1"]));
        Assert.Equal(SubagentStatus.Resumed, Assert.Single(view.Subagents).Status);

        // The next run announces the same ID: a continuation of the same entry, never a duplicate.
        view = Fold(view, Run("r-2"), new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" });
        Assert.Equal(SubagentStatus.Running, Assert.Single(view.Subagents).Status);

        view = Fold(view, new SubagentFinishedEvent { SubagentRunId = "parts-1" });
        var finished = Assert.Single(view.Subagents);
        Assert.Equal((SubagentStatus.Succeeded, "r-1"), (finished.Status, finished.RunId));
        Assert.Empty(finished.InterruptIds);
    }

    [Fact]
    public void A_run_that_sent_other_state_or_none_moves_the_baseline_its_deltas_apply_to()
    {
        var view = ConversationReducer.Reduce(Start, new StateEdited(new JsonObject { ["activeWorkOrder"] = "WO-1001" }));

        // The same state the view holds: nothing changes (the default, SendState on).
        Assert.Same(view, ConversationReducer.Reduce(view, new RunInputSent(new RunAgentInput { ThreadId = "t-1", RunId = "r-1", State = Json("""{"activeWorkOrder":"WO-1001"}""") })));

        // No state: the agent's deltas apply to {}, so the view's do too.
        var none = ConversationReducer.Reduce(view, new RunInputSent(new RunAgentInput { ThreadId = "t-1", RunId = "r-1" }));
        none = Fold(none, Run("r-1"), new StateDeltaEvent { Delta = Json("""[{"op":"add","path":"/step","value":1}]""") });
        Assert.Equal("""{"step":1}""", none.State.Document!.ToJsonString());

        // Other state (RunInputs.State): the view adopts what the agent was given.
        var other = ConversationReducer.Reduce(view, new RunInputSent(new RunAgentInput { ThreadId = "t-1", RunId = "r-1", State = Json("""{"activeWorkOrder":"WO-1003"}""") }));
        Assert.Equal("WO-1003", other.State.Document!["activeWorkOrder"]!.GetValue<string>());
    }

    private static ConversationView Fold(ConversationView view, params BaseEvent[] events)
    {
        foreach (var evt in events)
        {
            // How AGUIChatClient delivers content-less events: an update whose RawRepresentation is the event.
            view = ConversationReducer.Reduce(view, new UpdateReceived(new ChatResponseUpdate { Role = ChatRole.Assistant, RawRepresentation = evt }));
        }

        return view;
    }

    private static ConversationView Fold(ConversationView view, params object[] items)
    {
        foreach (var item in items)
        {
            view = item switch
            {
                BaseEvent evt => Fold(view, evt),
                BaseEvent[] events => Fold(view, events),
                ChatMessage message => ConversationReducer.Reduce(view, new MessageSent(message)),
                _ => throw new ArgumentException($"Can't fold a {item.GetType().Name}."),
            };
        }

        return view;
    }

    private static RunStartedEvent Run(string runId) => new() { ThreadId = "t-1", RunId = runId };

    private static ChatMessage User(string id, string text) => new(ChatRole.User, text) { MessageId = id };

    private static BaseEvent[] Text(string id, string text) =>
    [
        new TextMessageStartEvent { MessageId = id, Role = AGUIRoles.Assistant },
        new TextMessageContentEvent { MessageId = id, Delta = text },
        new TextMessageEndEvent { MessageId = id },
    ];

    private static MessagesSnapshotEvent Snapshot(params AGUIMessage[] messages) => new() { Messages = [.. messages] };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
