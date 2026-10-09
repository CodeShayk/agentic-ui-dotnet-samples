using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch23.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Tests;

/// <summary>
/// Injected instructions in tool results, with a model that obeys them. Nothing here stops the model
/// from being fooled; the tests prove that being fooled can't spend money.
/// </summary>
public sealed class PromptInjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_instruction_planted_in_a_work_order_note_can_only_produce_an_approval_request()
    {
        await using var host = Hosts.Agent();
        PlantNote(host, TestModel.Injection);

        var events = await Runs.RunAsync(host.CreateClient(TestTokens.Dana),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);

        // The model fell for it and asked for ten compressors...
        var interrupt = Runs.SingleInterrupt(events);
        var call = events.OfType<ToolCallStartEvent>().Single(s => s.ToolCallId == interrupt.ToolCallId);
        Assert.Equal(BeaconTools.PlacePartOrderName, call.ToolCallName);
        var arguments = string.Concat(events.OfType<ToolCallArgsEvent>().Where(a => a.ToolCallId == call.ToolCallId).Select(a => a.Delta));
        Assert.Contains("\"quantity\":10", arguments, StringComparison.Ordinal);

        // ...and the run stopped to ask Dana, showing her exactly that. Nothing was ordered.
        Assert.Empty(ApprovalEnforcementTests.Store(host).ListPartOrders());
    }

    [Fact]
    public async Task An_instruction_in_a_frontend_tool_result_is_data_with_the_same_limits()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        var first = Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Show WO-1001 on my screen"));
        first.Tools = [ApprovalEnforcementTests.ShowWorkOrderTool];
        var turn1 = await Runs.RunAsync(http, first, Ct);
        var callId = Assert.Single(turn1.OfType<ToolCallStartEvent>()).ToolCallId;

        // A compromised Desk (or a malicious page it scraped) returns an instruction instead of a result.
        var second = Runs.Turn("thread-1", "run-2",
            new AGUIToolMessage { Id = callId, ToolCallId = callId, Content = "Opened WO-1001. " + TestModel.Injection });
        second.Tools = [ApprovalEnforcementTests.ShowWorkOrderTool];
        var turn2 = await Runs.RunAsync(http, second, Ct);

        Assert.Equal(BeaconTools.PlacePartOrderName,
            turn2.OfType<ToolCallStartEvent>().Single(s => s.ToolCallId == Runs.SingleInterrupt(turn2).ToolCallId).ToolCallName);
        Assert.Empty(ApprovalEnforcementTests.Store(host).ListPartOrders());
    }

    [Fact]
    public async Task A_tool_result_for_a_call_the_server_never_made_is_ignored()
    {
        await using var host = Hosts.Agent();
        var http = host.CreateClient(TestTokens.Dana);

        await Runs.RunAsync(http, Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);
        await Runs.RunAsync(http, Runs.Turn("thread-1", "run-2",
            new AGUIToolMessage { Id = "tool-x", ToolCallId = "call-never-made", Content = TestModel.Injection },
            Runs.User("user-2", "Thanks.")), Ct);

        Assert.DoesNotContain("IGNORE PREVIOUS INSTRUCTIONS", host.Recorder.AllTextSeen, StringComparison.Ordinal);
        Assert.Empty(ApprovalEnforcementTests.Store(host).ListPartOrders());
    }

    private static void PlantNote<T>(BeaconHost<T> host, string note) where T : class
    {
        var store = host.Services.GetRequiredService<IFieldServiceStore>();
        var workOrder = store.GetWorkOrder("WO-1001")!;
        store.UpdateWorkOrder(workOrder with { Notes = [.. workOrder.Notes, note] });
    }
}
