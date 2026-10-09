using AGUI.Abstractions;
using Beacon.Testing;
using Ch21.Beacon.Agent.Interop;

namespace Ch21.Tests;

/// <summary>How calls to the manuals MCP server and Northwind's A2A agent appear on Beacon's AG-UI stream.</summary>
public sealed class StreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_run_reaches_both_systems_as_tool_calls_and_attributes_the_supplier_as_a_subagent()
    {
        await using var systems = InteropSystems.Start();

        var events = await AGUIWire.RunAsync(systems.Beacon.CreateClient(), "/agent", AGUIWire.UserRun(Wire.CombinedQuestion), Ct);

        Wire.AssertWellFormed(events);
        Assert.Equal(AGUIProtocol.Version, ((RunStartedEvent)events[0]).ProtocolVersion); // declared since Chapter 19
        Assert.Equal(["search_manual", "check_supplier_stock", "get_manual_section"], Wire.ToolCalls(events));

        // The supplier's tool call spawns a subagent invocation, closed just before the call's result.
        var supplierCall = events.OfType<ToolCallStartEvent>().Single(e => e.ToolCallName == SupplierAgentTool.Name);
        var started = Assert.Single(events.OfType<SubagentStartedEvent>());
        Assert.Equal("northwind-supply", started.Name);
        Assert.Equal(supplierCall.ToolCallId, started.ParentToolCallId);
        Assert.Equal(supplierCall.ParentMessageId, started.ParentMessageId);

        var finished = Assert.Single(events.OfType<SubagentFinishedEvent>());
        Assert.Equal(started.SubagentRunId, finished.SubagentRunId);
        var supplierResult = events.OfType<ToolCallResultEvent>().Single(e => e.ToolCallId == supplierCall.ToolCallId);
        Assert.Equal(events.ToList().IndexOf(finished) + 1, events.ToList().IndexOf(supplierResult));

        // Both results are Beacon's shapes, labeled with where they came from.
        var section = Wire.Result(events, "get_manual_section");
        Assert.Equal(ManualsToolSource.Source, section.GetProperty("source").GetString());
        Assert.False(section.TryGetProperty("error", out _)); // set only when the server reported an error
        Assert.Equal(ExternalContent.Notice, section.GetProperty("notice").GetString());
        Assert.Contains("RT-120/7.3", section.GetProperty("content").GetString());

        var stock = Wire.Result(events, SupplierAgentTool.Name);
        Assert.Equal(SupplierAgentTool.Source, stock.GetProperty("source").GetString());
        Assert.True(stock.GetProperty("canSupply").GetBoolean());
        Assert.Equal(1, stock.GetProperty("leadTimeBusinessDays").GetInt32());

        Assert.Contains("Northwind Supply can ship 1 x CMP-RT120 from Albany, NY", Wire.Text(events));
        Assert.IsType<RunFinishedSuccessOutcome>(((RunFinishedEvent)events[^1]).Outcome);
    }

    [Fact]
    public async Task Neither_protocols_envelope_reaches_the_AG_UI_stream()
    {
        await using var systems = InteropSystems.Start();

        var frames = await AGUIWire.RunRawAsync(systems.Beacon.CreateClient(), "/agent", AGUIWire.UserRun(Wire.CombinedQuestion), Ct);
        var wire = string.Join('\n', frames.Select(f => f.GetRawText()));

        // MCP's JSON-RPC envelope and content blocks, A2A's task bookkeeping: none of it crosses.
        Assert.DoesNotContain("jsonrpc", wire);
        Assert.DoesNotContain("isError", wire);
        Assert.DoesNotContain("contextId", wire);
        Assert.DoesNotContain("TASK_STATE", wire);
        Assert.DoesNotContain("artifact", wire, StringComparison.OrdinalIgnoreCase);

        // And the injected subagent events keep AG-UI 1.0's rule: optional fields are omitted, never null.
        Assert.All(frames, f => Assert.Empty(AGUIWire.NullProperties(f)));
    }
}
