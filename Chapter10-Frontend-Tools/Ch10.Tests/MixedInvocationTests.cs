using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Beacon.Testing;
using Ch10.Beacon.Desk.Screen;
using Ch10.Beacon.Desk.Tools;
using Ch10.Field.Tools;
using Microsoft.Extensions.AI;

namespace Ch10.Tests;

/// <summary>
/// A frontend tool and a backend tool in one turn, against the real agent in-process, through the client
/// stack Beacon Desk and Beacon Field use.
/// </summary>
public sealed class MixedInvocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Desk_runs_its_tool_here_and_the_agent_runs_its_own_in_the_next_run_of_the_same_turn()
    {
        await using var host = new AgentHost();
        var screen = new DeskScreen();
        var (conversation, transport, client) = host.CreateConversation(new DeskTools(screen, new InMemoryFieldServiceStore()).Create());
        using var _ = conversation;
        using var __ = client;

        await conversation.SendAsync("Open WO-1001 and check the warranty on its unit", cancellationToken: Ct);

        // One turn, two runs, one thread.
        Assert.Equal(2, transport.Runs.Count);
        Assert.Equal(transport.Runs[0].Input.ThreadId, transport.Runs[1].Input.ThreadId);

        // Run 1 declares the Desk's tools and ends with both calls unanswered: the agent's loop stopped at
        // the client's tool before running its own, and the run finished with success, not an interrupt.
        var (firstInput, firstEvents) = transport.Runs[0];
        Assert.Equal(["open_work_order", "highlight_schedule_slot", "prefill_part_order"], firstInput.Tools!.Select(t => t.Name));
        Assert.Equal(
            ["RUN_STARTED", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "RUN_FINISHED"],
            AGUIWire.Types(firstEvents));
        Assert.Equal(["open_work_order", "check_warranty"], firstEvents.OfType<ToolCallStartEvent>().Select(e => e.ToolCallName));
        Assert.IsType<RunFinishedSuccessOutcome>(Assert.IsType<RunFinishedEvent>(firstEvents[^1]).Outcome);

        // Run 2 answers the client's call only; the agent then runs check_warranty itself and answers.
        var (secondInput, secondEvents) = transport.Runs[1];
        var answer = Assert.IsType<AGUIToolMessage>(secondInput.Messages[^1]);
        var openCall = firstEvents.OfType<ToolCallStartEvent>().First().ToolCallId;
        Assert.Equal(openCall, answer.ToolCallId);
        using (var content = JsonDocument.Parse(answer.Content.ToString()))
        {
            Assert.Equal("opened", content.RootElement.GetProperty("status").GetString());
            Assert.Equal("WO-1001", content.RootElement.GetProperty("workOrderId").GetString());
        }

        Assert.Single(secondInput.Messages.OfType<AGUIToolMessage>());
        var result = Assert.Single(secondEvents.OfType<ToolCallResultEvent>());
        Assert.Equal(firstEvents.OfType<ToolCallStartEvent>().Last().ToolCallId, result.ToolCallId);

        // The screen changed here; the warranty came from the agent.
        Assert.Equal("WO-1001", screen.State.OpenWorkOrderId);
        var view = conversation.View;
        Assert.Equal(
            [("open_work_order", ToolExecutor.Client, ToolCallStatus.Completed), ("check_warranty", ToolExecutor.Agent, ToolCallStatus.Completed)],
            view.ToolCalls.Select(c => (c.Name, c.Executor, c.Status)));
        var text = view.Messages.OfType<AssistantMessageView>().Last(m => m.Text.Length > 0).Text;
        Assert.Contains("**WO-1001** is open on your screen.", text);
        Assert.Contains("EQ-501 is under warranty", text);
        Assert.Equal(ConversationStatus.Idle, view.Status);
    }

    [Fact]
    public async Task A_second_function_invocation_loop_finds_every_call_already_handled()
    {
        await using var host = new AgentHost();
        var runs = 0;
        var openWorkOrder = AIFunctionFactory.Create(
            (string workOrderId) =>
            {
                Interlocked.Increment(ref runs);
                return new Dictionary<string, string> { ["status"] = "opened", ["workOrderId"] = workOrderId };
            },
            "open_work_order", "Opens a work order in the dispatcher's work area.");

        // The wiring Listing 10.4 warns against: another UseFunctionInvocation() around AGUIChatClient.
        var tap = WireEventTap.ForHttp(host.CreateHttpClient(), "/agent");
        var transport = new RecordingTransport(tap);
        using var outer = new ChatClientBuilder(new AGUIChatClient(new AGUIChatClientOptions { Transport = transport }))
            .UseFunctionInvocation()
            .Build();
        using var conversation = new AgentConversation(outer, new ConversationOptions { Tools = [openWorkOrder], Tap = tap });

        await conversation.SendAsync("Open WO-1001 and check the warranty on its unit", cancellationToken: Ct);

        // The inner loop ran the tool and marked the call InformationalOnly, so the outer one skipped it:
        // one execution, the same two runs. The second loop changed nothing but added settings of its own.
        Assert.Equal(1, runs);
        Assert.Equal(2, transport.Runs.Count);
        Assert.All(conversation.View.ToolCalls, c => Assert.Equal(ToolCallStatus.Completed, c.Status));
    }

    [Fact]
    public async Task Field_reads_the_label_here_and_the_agent_checks_stock_with_what_it_read()
    {
        await using var host = new AgentHost();
        var rig = FieldRig.Create(label: "  cap-45-5 ");
        var (conversation, transport, client) = host.CreateConversation(rig.Tools.Create());
        using var _ = conversation;
        using var __ = client;

        await conversation.SendAsync("Scan the part label and check stock", cancellationToken: Ct);

        Assert.Equal(2, transport.Runs.Count);
        Assert.Equal(["scan_part_label"], transport.Runs[0].Events.OfType<ToolCallStartEvent>().Select(e => e.ToolCallName));
        Assert.Empty(transport.Runs[0].Events.OfType<ToolCallResultEvent>());

        // The agent searched with the normalized part number the device sent, not with what was typed.
        var search = Assert.Single(conversation.View.ToolCalls, c => c.Name == "search_parts");
        Assert.Equal((ToolExecutor.Agent, "CAP-45-5"), (search.Executor, search.Arguments!.Value.GetProperty("query").GetString()));
        var text = string.Concat(conversation.View.Messages.OfType<AssistantMessageView>().Select(m => m.Text));
        Assert.Contains("The label reads CAP-45-5.", text);
        Assert.Contains("| CAP-45-5 | Dual run capacitor 45/5 uF | $38.50 | 24 |", text);
    }

    [Fact]
    public async Task Field_check_in_combines_the_device_location_with_the_work_order_site()
    {
        await using var host = new AgentHost();
        var rig = FieldRig.Create(consent: [ConsentDecision.AllowForSession]);
        var (conversation, _, client) = host.CreateConversation(rig.Tools.Create());
        using var __ = conversation;
        using var ___ = client;

        await conversation.SendAsync("Am I at the right site for WO-1001?", cancellationToken: Ct);

        var answer = conversation.View.Messages.OfType<AssistantMessageView>().Last(m => m.Text.Length > 0).Text;
        Assert.Equal("You're at Harbor View Dental (12 Wharf Street, Portsmouth), the site for WO-1001 (about 32 m from the address on file).", answer);
        Assert.Equal(1, rig.Location.Reads);
        Assert.True(rig.Session.IsAllowedForSession(DeviceCapability.Location));
    }
}
