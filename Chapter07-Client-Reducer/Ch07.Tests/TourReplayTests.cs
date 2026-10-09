using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>
/// One captured stream per AG-UI event family, from Chapter 3's event tour, replayed through the real
/// AGUIChatClient and AgentConversation. Each test asserts on the view a UI would render.
/// </summary>
public sealed class TourReplayTests
{
    [Fact]
    public async Task Text_run_becomes_one_finished_assistant_message()
    {
        using var harness = await Harness.ReplayAsync("tour-text.sse");

        var answer = harness.Message<AssistantMessageView>("msg-r1-1");
        Assert.Equal("I'm Beacon, Contoso Climate's field-service assistant. Ask me about a work order, a part or today's schedule.", answer.Text);
        Assert.False(answer.IsStreaming);
        Assert.False(answer.IsPartial);

        Assert.Equal(ConversationStatus.Idle, harness.View.Status);
        var run = Assert.Single(harness.View.Runs);
        Assert.Equal(("r-1", RunOutcome.Succeeded, "1.0"), (run.RunId, run.Outcome, run.ProtocolVersion));
        Assert.Equal(["user-1", "msg-r1-1"], harness.View.Messages.Select(m => m.Id));
    }

    [Fact]
    public async Task Backend_tool_call_lands_in_its_message_with_its_result_step_and_usage()
    {
        using var harness = await Harness.ReplayAsync("tour-tools.sse");
        var view = harness.View;

        // The call belongs to the message that announced it, and its result is a tool message.
        Assert.Equal(["user-1", "msg-r2-1", "result-r2-1", "msg-r2-2"], view.Messages.Select(m => m.Id));
        Assert.Equal(["call-r2-1"], harness.Message<AssistantMessageView>("msg-r2-1").ToolCallIds);

        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("get_work_order", ToolCallStatus.Completed, ToolExecutor.Agent), (call.Name, call.Status, call.Executor));
        Assert.Equal("WO-1001", call.Arguments!.Value.GetProperty("workOrderId").GetString());
        Assert.Contains("\"status\":\"Scheduled\"", call.Result);

        var step = Assert.Single(view.Steps);
        Assert.Equal(("look-up", false), (step.Name, step.IsActive));

        var usage = Assert.Single(view.CurrentRun!.Usage);
        Assert.Equal((1336L, 1024L), (usage.TotalTokens, usage.CachedInputTokens));
    }

    [Fact]
    public async Task With_a_tap_the_tool_call_is_visible_while_its_arguments_stream()
    {
        using var harness = await Harness.ReplayAsync("tour-tools.sse", tap: true);

        var states = CallStates(harness, "call-r2-1");

        // TOOL_CALL_START, each TOOL_CALL_ARGS and TOOL_CALL_END each show up on screen.
        Assert.Contains((ToolCallStatus.Streaming, "{\"workOrderId\":"), states);
        Assert.Contains((ToolCallStatus.InProgress, "{\"workOrderId\":\"WO-1001\"}"), states);
        Assert.Equal(ToolCallStatus.Completed, states[^1].Status);
    }

    [Fact]
    public async Task Without_a_tap_the_tool_call_first_appears_with_its_arguments_complete()
    {
        using var harness = await Harness.ReplayAsync("tour-tools.sse", tap: false);

        var states = CallStates(harness, "call-r2-1");

        // AGUIChatClient yields nothing for TOOL_CALL_START or TOOL_CALL_ARGS and holds the call back
        // until its result arrives: the call is born complete and finishes on the very next update.
        Assert.DoesNotContain(states, s => s.Status == ToolCallStatus.Streaming);
        Assert.Equal(
            [(ToolCallStatus.InProgress, "{\"workOrderId\":\"WO-1001\"}"), (ToolCallStatus.Completed, "{\"workOrderId\":\"WO-1001\"}")],
            states);
    }

    [Fact]
    public async Task State_snapshot_then_delta_builds_the_checklist()
    {
        using var harness = await Harness.ReplayAsync("tour-state.sse");
        var state = harness.View.State;

        Assert.True(state.HasValue);
        Assert.Equal((2, StateChangeSource.Agent, false), (state.Version, state.LastChangedBy, state.NeedsResync));
        Assert.Equal("WO-1001", state.Document!["workOrderId"]!.GetValue<string>());
        Assert.Equal(["C1", "C2", "C3", "C4"], state.Document["checklist"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Replayed_history_reconciles_the_transcript()
    {
        // Two runs on one stream: the earlier run restated as MESSAGES_SNAPSHOT, then the requested run.
        using var harness = await Harness.ReplayAsync("tour-history.sse", prompt: "Restore the WO-1001 conversation");
        var view = harness.View;

        // The snapshot doesn't contain the question the client just sent, so the client drops it: the
        // producer is declaring the complete set. The four restated messages arrive in snapshot order.
        Assert.Equal(["user-r4prev-1", "msg-r4prev-1", "result-r4prev-1", "msg-r4prev-2", "msg-r4-1"], view.Messages.Select(m => m.Id));
        Assert.Equal(new ReconciliationSummary(Replaced: 0, Changed: 0, Added: 4, Dropped: 1, Kept: 0), view.LastReconciliation);

        var restoredCall = Assert.Single(view.ToolCalls);
        Assert.Equal(("call-r4prev-1", ToolCallStatus.Completed), (restoredCall.Id, restoredCall.Status));
        Assert.Equal(["r-4-prev", "r-4"], view.Runs.Select(r => r.RunId));
        Assert.All(view.Runs, r => Assert.Equal(RunOutcome.Succeeded, r.Outcome));
    }

    [Fact]
    public async Task Activity_snapshot_and_delta_build_one_widget_in_sequence()
    {
        using var harness = await Harness.ReplayAsync("tour-activity.sse");

        Assert.Equal(["user-1", "activity-r5-1", "msg-r5-1"], harness.View.Messages.Select(m => m.Id));
        var activity = harness.Message<ActivityMessageView>("activity-r5-1");
        Assert.Equal("parts-search", activity.ActivityType);
        Assert.Equal((1, "done"), (activity.Content["found"]!.GetValue<int>(), activity.Content["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Reasoning_message_keeps_its_encrypted_value_and_the_span_closes()
    {
        using var harness = await Harness.ReplayAsync("tour-reasoning.sse");

        var reasoning = harness.Message<ReasoningMessageView>("reasoning-r6-1");
        Assert.StartsWith("Short-cycling with warm air", reasoning.Text);
        Assert.False(reasoning.IsStreaming);
        Assert.Equal("gAAAAABpQ2hhcHRlci0zLW9wYXF1ZS1yZWFzb25pbmctYXJ0aWZhY3Q=", reasoning.EncryptedValue);
        Assert.False(harness.View.IsReasoning);
        Assert.Equal(288L, harness.View.CurrentRun!.Usage[0].ReasoningTokens);

        // Reasoning was open, then closed: a "thinking" indicator had something to show.
        Assert.Contains(harness.Published, p => p.View.IsReasoning);
    }

    [Fact]
    public async Task Subagent_work_is_attributed_and_a_failed_specialist_does_not_fail_the_run()
    {
        using var harness = await Harness.ReplayAsync("tour-subagents.sse");
        var view = harness.View;

        Assert.Equal("sub-parts-r7-1", harness.Message<AssistantMessageView>("msg-r7-2").SubagentRunId);
        Assert.Equal("sub-warranty-r7-1", harness.Message<AssistantMessageView>("msg-r7-3").SubagentRunId);
        Assert.Null(harness.Message<AssistantMessageView>("msg-r7-4").SubagentRunId);

        // msg-r7-2's content events carry no tag; they continue whatever their opener owns.
        Assert.Equal("CAP-45-5 fits the CA-36X; 24 are in stock in Portsmouth.", harness.Message<AssistantMessageView>("msg-r7-2").Text);

        Assert.Equal(
            [("parts-specialist", SubagentStatus.Succeeded), ("warranty-specialist", SubagentStatus.Failed)],
            view.Subagents.Select(s => (s.Name, s.Status)));
        Assert.Equal("warranty_timeout", view.Subagents[1].ErrorCode);
        Assert.Equal(24, view.Subagents[0].Result!.Value.GetProperty("inStock").GetInt32());

        Assert.Equal(ConversationStatus.Idle, view.Status);
    }

    [Fact]
    public async Task Raw_and_custom_events_are_kept_for_whoever_wants_them()
    {
        using var harness = await Harness.ReplayAsync("tour-passthrough.sse");

        var raw = Assert.Single(harness.View.RawEvents);
        Assert.Equal("contoso-inventory", raw.Source);
        var custom = Assert.Single(harness.View.CustomEvents);
        Assert.Equal("contoso.beacon.notify", custom.Name);
        Assert.Equal("TECH-1", custom.Value!.Value.GetProperty("technicianId").GetString());
    }

    [Fact]
    public async Task Chunks_are_expanded_by_the_reducer()
    {
        using var harness = await Harness.ReplayAsync("tour-chunks.sse");
        var view = harness.View;

        Assert.Equal("The dispatcher wants a summary, then the work order on screen.", harness.Message<ReasoningMessageView>("reasoning-r9-1").Text);
        var brief = harness.Message<AssistantMessageView>("msg-r9-1");
        Assert.Equal(("WO-1001: Harbor View Dental, EQ-501 short-cycling, Priya at 9:00.", false), (brief.Text, brief.IsStreaming));

        // A frontend call the run left for the application, as pendingToolCallIds says.
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("show_work_order", ToolCallStatus.AwaitingClient, "msg-r9-1"), (call.Name, call.Status, call.ParentMessageId));
        Assert.Equal("WO-1001", call.Arguments!.Value.GetProperty("workOrderId").GetString());
        Assert.Equal(["call-r9-1"], view.CurrentRun!.PendingToolCallIds);
    }

    [Fact]
    public async Task Chunked_text_never_reaches_update_Text()
    {
        // The reason the reducer reads RawRepresentation: AGUIChatClient 1.0.0 passes *_CHUNK events
        // through as content-less updates, so a client reading update.Text sees nothing at all.
        using IChatClient client = new AGUIChatClient(new AGUIChatClientOptions
        {
            Transport = new ReplayTransport(await Recordings.LoadAsync("tour-chunks.sse")),
        });

        var text = "";
        var chunkUpdates = 0;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Brief me") { MessageId = "user-1" }],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            text += update.Text;
            chunkUpdates += update.RawRepresentation is TextMessageChunkEvent && update.Contents.Count == 0 ? 1 : 0;
        }

        Assert.Equal("", text);
        Assert.Equal(3, chunkUpdates);
    }

    [Fact]
    public async Task Approval_interrupt_waits_for_the_dispatcher()
    {
        using var harness = await Harness.ReplayAsync("tour-interrupt.sse");
        var view = harness.View;

        Assert.Equal(ConversationStatus.AwaitingInput, view.Status);
        Assert.Equal(RunOutcome.Interrupted, view.CurrentRun!.Outcome);

        var interrupt = Assert.Single(view.OpenInterrupts);
        Assert.Equal(("approval-r10-1", InterruptKind.ToolApproval, "call-r10-1"), (interrupt.Id, interrupt.Kind, interrupt.ToolCallId));
        Assert.Equal("Approve ordering 1 x CMP-CA36 ($1,795.00) for WO-1001?", interrupt.Message);
        Assert.Equal("2026-11-16T17:00:00-05:00", interrupt.ExpiresAt);
        Assert.Equal("boolean", interrupt.ResponseSchema!.Value.GetProperty("properties").GetProperty("approved").GetProperty("type").GetString());
        Assert.IsType<ToolApprovalRequestContent>(interrupt.Request);

        var call = Assert.Single(view.ToolCalls);
        Assert.Equal(("place_part_order", ToolCallStatus.AwaitingApproval, ToolExecutor.Agent), (call.Name, call.Status, call.Executor));
    }

    [Fact]
    public async Task Cancelled_run_is_neither_a_success_nor_a_failure()
    {
        using var harness = await Harness.ReplayAsync("tour-cancelled.sse");

        Assert.Equal(ConversationStatus.Cancelled, harness.View.Status);
        Assert.Equal(RunOutcome.Cancelled, harness.View.CurrentRun!.Outcome);
        Assert.Null(harness.View.Failure);
        Assert.Equal(1116L, harness.View.CurrentRun.Usage[0].TotalTokens);
        Assert.Equal(["draft-report"], harness.View.Steps.Where(s => !s.IsActive).Select(s => s.Name));
    }

    [Fact]
    public async Task Run_error_fails_the_turn_and_keeps_the_partial_text()
    {
        using var harness = await Harness.ReplayAsync("tour-error.sse");
        var view = harness.View;

        Assert.Equal(ConversationStatus.Failed, view.Status);
        Assert.Equal(new RunFailure(FailureKind.AgentError, "The parts inventory service did not respond.") { Code = "inventory_unavailable" }, view.Failure);
        Assert.Equal(RunOutcome.Failed, view.CurrentRun!.Outcome);

        var partial = harness.Message<AssistantMessageView>("msg-r12-1");
        Assert.Equal(("Checking the parts inventory for ", true, false), (partial.Text, partial.IsPartial, partial.IsStreaming));
    }

    private static List<(ToolCallStatus Status, string Arguments)> CallStates(Harness harness, string callId)
    {
        List<(ToolCallStatus, string)> states = [];
        foreach (var (_, view) in harness.Published)
        {
            if (view.FindToolCall(callId) is { } call && (states.Count == 0 || states[^1] != (call.Status, call.ArgumentsText)))
            {
                states.Add((call.Status, call.ArgumentsText));
            }
        }

        return states;
    }
}
