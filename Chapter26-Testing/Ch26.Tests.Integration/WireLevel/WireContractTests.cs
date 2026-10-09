using Beacon.Testing;
using Ch26.Tests.Integration.Infrastructure;

namespace Ch26.Tests.Integration.WireLevel;

/// <summary>
/// Section 26.3: what Beacon promises on the wire, asserted property by property with JsonDocument. Each
/// test states one invariant a client depends on, so a failure names the broken promise.
/// </summary>
public sealed class WireContractTests
{
    [Theory]
    [MemberData(nameof(Scenarios.All), MemberType = typeof(Scenarios))]
    public async Task Every_run_starts_with_RUN_STARTED_and_ends_with_its_terminal_event(string scenario)
    {
        foreach (var run in await Scenarios.RunAsync(scenario))
        {
            var types = Wire.Types(run.Frames);
            Assert.Equal("RUN_STARTED", types[0]);
            Assert.Equal("RUN_FINISHED", types[^1]);
            Assert.Single(types, t => t is "RUN_FINISHED" or "RUN_ERROR");

            var input = run.InputJson;
            foreach (var boundary in new[] { run.Frames[0], run.Frames[^1] })
            {
                Assert.Equal(input.GetProperty("threadId").GetString(), boundary.GetProperty("threadId").GetString());
                Assert.Equal(input.GetProperty("runId").GetString(), boundary.GetProperty("runId").GetString());
            }
        }
    }

    [Fact]
    public async Task An_order_over_the_limit_ends_the_run_with_an_approval_interrupt()
    {
        var run = Assert.Single(await Scenarios.RunAsync(Scenarios.Order));

        var start = Assert.Single(run.Frames.OfType("TOOL_CALL_START"));
        Assert.Equal("place_part_order", start.GetProperty("toolCallName").GetString());
        Assert.Empty(run.Frames.OfType("TOOL_CALL_RESULT")); // proposed, never executed

        var outcome = run.Frames[^1].GetProperty("outcome");
        Assert.Equal("interrupt", outcome.GetProperty("type").GetString());
        var interrupt = Assert.Single(outcome.GetProperty("interrupts").EnumerateArray());
        Assert.StartsWith("apr_", interrupt.GetProperty("id").GetString());          // issued by the server's ledger
        Assert.Equal("tool_call", interrupt.GetProperty("reason").GetString());
        Assert.Equal(start.GetProperty("toolCallId").GetString(), interrupt.GetProperty("toolCallId").GetString());
    }

    [Fact]
    public async Task The_work_order_state_settles_inside_the_run()
    {
        var run = Assert.Single(await Scenarios.RunAsync(Scenarios.Status));
        var types = Wire.Types(run.Frames);

        var snapshotAt = types.ToList().IndexOf("STATE_SNAPSHOT");
        Assert.InRange(snapshotAt, 0, types.Count - 2); // before RUN_FINISHED, which is last

        var workOrder = run.Frames[snapshotAt].GetProperty("snapshot").GetProperty("activeWorkOrder");
        Assert.Equal("WO-1001", workOrder.GetProperty("id").GetString());
        Assert.Equal("Harbor View Dental", workOrder.GetProperty("customer").GetString());
        Assert.Equal("EQ-501", workOrder.GetProperty("equipmentId").GetString());
    }

    [Theory]
    [MemberData(nameof(Scenarios.All), MemberType = typeof(Scenarios))]
    public async Task No_optional_field_is_written_as_null(string scenario)
    {
        foreach (var run in await Scenarios.RunAsync(scenario))
        {
            Assert.All(run.Frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
        }
    }
}
