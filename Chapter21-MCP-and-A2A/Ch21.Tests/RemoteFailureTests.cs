using AGUI.Abstractions;
using Beacon.Testing;
using Ch21.Beacon.Agent.Interop;
using Microsoft.Extensions.Logging;

namespace Ch21.Tests;

/// <summary>What the user sees, and what the server keeps, when a system behind the agent is down.</summary>
public sealed class RemoteFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_supplier_outage_is_a_SUBAGENT_ERROR_and_the_run_still_finishes()
    {
        await using var systems = InteropSystems.Start();
        systems.SupplierLink.Down = true;

        var frames = await AGUIWire.RunRawAsync(
            systems.Beacon.CreateClient(), "/agent", AGUIWire.UserRun("Ask Northwind about the lead time for EXP-TANK-4."), Ct);
        var events = await AGUIWire.RunAsync(
            systems.Beacon.CreateClient(), "/agent", AGUIWire.UserRun("Ask Northwind about the lead time for EXP-TANK-4."), Ct);

        Wire.AssertWellFormed(events);
        var error = Assert.Single(events.OfType<SubagentErrorEvent>());
        Assert.Equal("Northwind Supply's agent didn't answer, so the supplier's stock is unknown.", error.Message);
        Assert.Equal("delegate_failed", error.Code);
        Assert.Equal(error.Message, Wire.Result(events, SupplierAgentTool.Name).GetProperty("error").GetString());

        // SUBAGENT_ERROR ends the invocation, not the run.
        Assert.IsType<RunFinishedSuccessOutcome>(((RunFinishedEvent)events[^1]).Outcome);

        // The exception stays in the server's log; the stream carries only Beacon's sentence.
        var wire = string.Join('\n', frames.Select(f => f.GetRawText()));
        Assert.DoesNotContain("HttpRequestException", wire);
        Assert.DoesNotContain("offline", wire);
        Assert.Contains(systems.Beacon.Logs, l => l.Level == LogLevel.Warning && l.Exception is HttpRequestException);
    }

    [Fact]
    public async Task With_the_manuals_server_down_runs_go_on_without_manual_tools()
    {
        await using var systems = InteropSystems.Start();
        systems.ManualsLink.Down = true;

        var events = await AGUIWire.RunAsync(
            systems.Beacon.CreateClient(), "/agent", AGUIWire.UserRun("Find the locked rotor procedure in the RT-120 manual."), Ct);

        Wire.AssertWellFormed(events);
        Assert.Empty(events.OfType<ToolCallStartEvent>());
        Assert.StartsWith("I can't reach the equipment manuals right now", Wire.Text(events));
        Assert.Contains(systems.Beacon.Logs, l => l.Message.Contains("is unavailable; runs continue without manual tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_outage_mid_conversation_becomes_a_tool_error_and_a_later_run_reconnects()
    {
        await using var systems = InteropSystems.Start(("Manuals:RetryAfterSeconds", "0"));
        var http = systems.Beacon.CreateClient();
        const string Question = "Find the locked rotor procedure in the RT-120 manual.";

        var first = await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(Question, runId: "run-1"), Ct);
        Assert.Equal(ManualsToolSource.Source, Wire.Result(first, "get_manual_section").GetProperty("source").GetString());

        // The connection is cached from run 1; the server goes away before run 2 calls it.
        systems.ManualsLink.Down = true;
        var second = await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(Question, runId: "run-2"), Ct);
        Wire.AssertWellFormed(second);
        Assert.Equal(
            "The equipment manuals service didn't answer. Try again in a minute.",
            Wire.Result(second, "search_manual").GetProperty("error").GetString());

        // Back up: the next run reconnects and lists the tools again.
        systems.ManualsLink.Down = false;
        var third = await AGUIWire.RunAsync(http, "/agent", AGUIWire.UserRun(Question, runId: "run-3"), Ct);
        Assert.Equal(["search_manual", "get_manual_section"], Wire.ToolCalls(third));
    }
}
