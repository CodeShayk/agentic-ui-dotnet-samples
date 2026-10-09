using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch07.Tests;

/// <summary>The conversation service: turns, Stop, the RunAgentInput hooks, frontend tools and resuming an interrupt.</summary>
public sealed class AgentConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stop_keeps_what_arrived_and_the_next_turn_sends_only_what_the_user_saw()
    {
        // The status run, cut off after the answer's third fragment and held open, as a slow model would.
        var status = await Recordings.LoadAsync("agent-wo-1001-status.sse");
        var cut = status.TakeWhile(e => e is not TextMessageContentEvent).Concat(status.OfType<TextMessageContentEvent>().Take(3)).ToList();
        var transport = new ReplayTransport(cut, await Recordings.LoadAsync("tour-text.sse")) { HoldOpen = true };
        using var harness = new Harness(transport);

        var turn = harness.Conversation.SendAsync("What is the status of WO-1001?", cancellationToken: Ct);
        await transport.Drained.Task.WaitAsync(Ct);
        Assert.True(harness.View.IsBusy);

        harness.Conversation.Stop();
        await turn;
        await transport.Cancelled.Task.WaitAsync(Ct); // the request was aborted, so the agent sees it

        var view = harness.View;
        Assert.Equal(ConversationStatus.Stopped, view.Status);
        Assert.Equal(RunOutcome.Abandoned, view.CurrentRun!.Outcome);
        var partial = harness.Message<AssistantMessageView>("msg-0002");
        Assert.Equal(("**WO-1001** is Scheduled (High priority): Air conditioner short-cycling and ", true), (partial.Text, partial.IsPartial));

        // The completed lookup goes back with its result; the cut-off answer goes back as the text Dana saw.
        await harness.Conversation.SendAsync("Hello", cancellationToken: Ct);
        Assert.Equal(["user", "assistant", "tool", "assistant", "user"], transport.Inputs[1].Messages.Select(m => m.Role));
        Assert.Equal(partial.Text, ((AGUIAssistantMessage)transport.Inputs[1].Messages[3]).Content);
    }

    [Fact]
    public async Task One_turn_at_a_time()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-text.sse")) { HoldOpen = true };
        using var harness = new Harness(transport);

        var first = harness.Conversation.SendAsync("Hello", cancellationToken: Ct);
        await transport.Drained.Task.WaitAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Conversation.SendAsync("Again", cancellationToken: Ct));
        Assert.Throws<InvalidOperationException>(() => harness.Conversation.Reset());

        harness.Conversation.Stop();
        await first;
        Assert.Equal(ConversationStatus.Stopped, harness.View.Status);
    }

    [Fact]
    public async Task Every_message_has_an_id_and_every_run_keeps_the_thread_and_sends_the_state()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-text.sse"), await Recordings.LoadAsync("tour-text.sse"));
        using var harness = new Harness(transport, threadId: "thread-desk-1");

        harness.Conversation.UpdateState(new JsonObject { ["activeWorkOrder"] = "WO-1001" });
        await harness.Conversation.SendAsync("Hello", cancellationToken: Ct);
        await harness.Conversation.SendAsync("Hello again", cancellationToken: Ct);

        Assert.All(transport.Inputs, input => Assert.Equal("thread-desk-1", input.ThreadId));
        Assert.NotEqual(transport.Inputs[0].RunId, transport.Inputs[1].RunId);
        Assert.All(transport.Inputs.SelectMany(i => i.Messages), m => Assert.False(string.IsNullOrEmpty(m.Id)));
        Assert.All(transport.Inputs, input => Assert.Equal("WO-1001", input.State!.Value.GetProperty("activeWorkOrder").GetString()));
        Assert.Equal(StateChangeSource.Client, harness.View.State.LastChangedBy);
    }

    [Fact]
    public async Task A_frontend_tool_runs_in_the_client_and_turn_inputs_go_on_the_first_run_only()
    {
        // Run 1: the agent calls the client's open_work_order tool and leaves it pending.
        // Run 2: AGUIChatClient's own function invocation ran it and asks again, in the same turn.
        BaseEvent[] callsTool =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "open_work_order", ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = "{\"workOrderId\":\"WO-1001\"}" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1", Outcome = new RunFinishedSuccessOutcome { PendingToolCallIds = ["call-1"] } },
        ];
        BaseEvent[] answers =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-2" },
            new TextMessageStartEvent { MessageId = "msg-2", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg-2", Delta = "WO-1001 is open on your screen." },
            new TextMessageEndEvent { MessageId = "msg-2" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-2" },
        ];

        string? opened = null;
        var openWorkOrder = AIFunctionFactory.Create(
            ([Description("The work order to open")] string workOrderId) => { opened = workOrderId; return $"Opened {workOrderId}."; },
            "open_work_order",
            "Opens a work order on the dispatcher's screen.");

        var transport = new ReplayTransport(callsTool, answers);
        using var harness = new Harness(transport, tools: [openWorkOrder],
            configureInput: (input, _) => input.Context = [new AGUIContext { Description = "screen", Value = "dispatch" }]);

        await harness.Conversation.SendAsync("Open WO-1001", new RunInputs { ParentRunId = "r-0" }, Ct);

        Assert.Equal("WO-1001", opened);
        Assert.Equal(2, transport.Inputs.Count);
        Assert.Equal(("r-0", (string?)null), (transport.Inputs[0].ParentRunId, transport.Inputs[1].ParentRunId));
        Assert.All(transport.Inputs, input => Assert.Equal("dispatch", Assert.Single(input.Context!).Value));
        Assert.Equal("open_work_order", Assert.Single(transport.Inputs[0].Tools!).Name);
        // AIFunctionFactory returns the string as a JSON value, and that is what goes on the wire: quoted.
        Assert.Equal("\"Opened WO-1001.\"", ((AGUIToolMessage)transport.Inputs[1].Messages[^1]).Content.ToString());

        var call = Assert.Single(harness.View.ToolCalls);
        Assert.Equal((ToolExecutor.Client, ToolCallStatus.Completed, "\"Opened WO-1001.\""), (call.Executor, call.Status, call.Result));
        Assert.Equal(ConversationStatus.Idle, harness.View.Status);
        Assert.Equal("WO-1001 is open on your screen.", harness.Message<AssistantMessageView>("msg-2").Text);

        // The status never fell back to Idle between the two runs of the turn.
        List<ConversationStatus> transitions = [];
        foreach (var (_, published) in harness.Published)
        {
            if (transitions.Count == 0 || transitions[^1] != published.Status)
            {
                transitions.Add(published.Status);
            }
        }

        Assert.Equal([ConversationStatus.Idle, ConversationStatus.Starting, ConversationStatus.Running, ConversationStatus.Idle], transitions);
    }

    [Fact]
    public async Task Answering_an_approval_resumes_the_run_with_the_request_and_the_response()
    {
        BaseEvent[] resumed =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-11" },
            new ToolCallResultEvent { MessageId = "result-r11-1", ToolCallId = "call-r10-1", Content = new AGUIContent("{\"id\":\"PO-7001\",\"status\":\"Placed\"}") },
            new TextMessageStartEvent { MessageId = "msg-r11-1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg-r11-1", Delta = "Order PO-7001 is placed." },
            new TextMessageEndEvent { MessageId = "msg-r11-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-11" },
        ];
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), resumed);
        using var harness = new Harness(transport);

        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);
        var approval = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(harness.View.OpenInterrupts).Request);

        await harness.Conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct);

        // AGUIChatClient strips the request and the response from the messages and sends a resume entry.
        var input = transport.Inputs[1];
        var resume = Assert.Single(input.Resume!);
        Assert.Equal(("approval-r10-1", ResumeStatus.Resolved), (resume.InterruptId, resume.Status));
        Assert.True(resume.Payload!.Value.GetProperty("approved").GetBoolean());
        Assert.Equal(["user", "assistant"], input.Messages.Select(m => m.Role));

        var view = harness.View;
        Assert.Empty(view.OpenInterrupts);
        Assert.Equal(ConversationStatus.Idle, view.Status);
        var call = Assert.Single(view.ToolCalls);
        Assert.Equal((ToolCallStatus.Completed, "{\"id\":\"PO-7001\",\"status\":\"Placed\"}"), (call.Status, call.Result));
    }

    [Fact]
    public async Task An_open_interrupt_must_be_answered_or_abandoned_before_the_agent_continues()
    {
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), await Recordings.LoadAsync("tour-text.sse"));
        using var harness = new Harness(transport);
        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);

        // Omission is not abandonment: a new message can't slip past the request, and nothing is sent.
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Conversation.SendAsync("Never mind", cancellationToken: Ct));
        Assert.Single(transport.Inputs);
        Assert.Equal(ConversationStatus.AwaitingInput, harness.View.Status);

        // Abandoning it explicitly is how the thread moves on.
        await harness.Conversation.ContinueAsync(
            new RunInputs { Resume = [new AGUIResume { InterruptId = "approval-r10-1", Status = ResumeStatus.Cancelled }] }, Ct);

        var resume = Assert.Single(transport.Inputs[1].Resume!);
        Assert.Equal(("approval-r10-1", ResumeStatus.Cancelled), (resume.InterruptId, resume.Status));
        Assert.True(harness.View.FindInterrupt("approval-r10-1")!.IsAnswered);
        Assert.Equal(ConversationStatus.Idle, harness.View.Status);
    }

    [Fact]
    public async Task An_expired_interrupt_can_only_be_abandoned()
    {
        // The tour's approval expires at 17:00 on November 16; it's now 18:00.
        var transport = new ReplayTransport(await Recordings.LoadAsync("tour-interrupt.sse"), await Recordings.LoadAsync("tour-text.sse"));
        using var harness = new Harness(transport, now: new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.FromHours(-5)));
        await harness.Conversation.SendAsync("Order a CMP-CA36 compressor for WO-1001", cancellationToken: Ct);
        var approval = (ToolApprovalRequestContent)harness.View.FindInterrupt("approval-r10-1")!.Request!;

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Conversation.RespondAsync([approval.CreateResponse(approved: true)], cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Conversation.ContinueAsync(
            new RunInputs { Resume = [new AGUIResume { InterruptId = "approval-r10-1", Status = ResumeStatus.Resolved }] }, Ct));
        Assert.Single(transport.Inputs);

        await harness.Conversation.ContinueAsync(
            new RunInputs { Resume = [new AGUIResume { InterruptId = "approval-r10-1", Status = ResumeStatus.Cancelled }] }, Ct);
        Assert.Equal(2, transport.Inputs.Count);
    }

    [Fact]
    public async Task An_answer_must_match_an_open_interrupt()
    {
        using var harness = new Harness(new ReplayTransport());
        var stray = new ToolApprovalResponseContent("approval-nobody-asked-for", approved: true, new FunctionCallContent("call-x", "place_part_order"));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Conversation.RespondAsync([stray], cancellationToken: Ct));
        Assert.Equal(ConversationStatus.Idle, harness.View.Status);
    }

    [Fact]
    public async Task An_unreachable_agent_is_a_failure_not_an_exception()
    {
        using var http = new HttpClient(new RefusingHandler());
        using var client = new AGUI.Client.AGUIChatClient(new AGUI.Client.AGUIChatClientOptions(http, "http://localhost:5070/agent"));
        using var conversation = new AgentConversation(client);

        await conversation.SendAsync("Hello", cancellationToken: Ct);

        Assert.Equal(ConversationStatus.Failed, conversation.View.Status);
        Assert.Equal(FailureKind.Unreachable, conversation.View.Failure!.Kind);
        Assert.Empty(conversation.View.Runs);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    [Fact]
    public async Task Changed_publishes_immutable_views()
    {
        using var harness = await Harness.ReplayAsync("tour-tools.sse");

        // TEXT_MESSAGE_START (seen through the tap) opens the message before any text arrives.
        var opened = harness.Published.First(p => p.View.FindMessage("msg-r2-1") is not null);
        Assert.IsType<WireEventObserved>(opened.Action);
        Assert.Equal(("", true), (((AssistantMessageView)opened.View.FindMessage("msg-r2-1")!).Text, ((AssistantMessageView)opened.View.FindMessage("msg-r2-1")!).IsStreaming));

        // Every published view is a distinct snapshot; earlier ones never change after the fact.
        var early = harness.Published.First(p => p.View.FindMessage("msg-r2-1") is AssistantMessageView { Text.Length: > 0 }).View;
        Assert.Equal("Let me pull ", ((AssistantMessageView)early.FindMessage("msg-r2-1")!).Text);
        Assert.Equal(harness.Published.Count, harness.Published.Select(p => p.View).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Same(harness.Conversation.View, harness.Published[^1].View);
    }
}
