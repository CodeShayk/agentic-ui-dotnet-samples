using AGUI.Abstractions;
using Ch18.Beacon.Desk.Activity;
using Ch18.Tests.Infrastructure;

namespace Ch18.Tests;

/// <summary>The board projection on streams Beacon's own agent doesn't produce but other producers do.</summary>
public sealed class SpecialistBoardTests
{
    [Fact]
    public void An_agent_called_as_a_tool_gets_a_lane_that_names_the_call()
    {
        // Chapter 21's shape: the parent's tool call, bracketed by the remote agent's invocation, nothing attributed inside.
        var view = Views.Fold(
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new ToolCallStartEvent { ToolCallId = "call-1", ToolCallName = "check_supplier_stock", ParentMessageId = "msg-1" },
            new ToolCallArgsEvent { ToolCallId = "call-1", Delta = """{"sku":"CMP-RT120","quantity":1}""" },
            new ToolCallEndEvent { ToolCallId = "call-1" },
            new SubagentStartedEvent { SubagentRunId = "northwind-supply-3f9a1c2e", Name = "northwind-supply", ParentToolCallId = "call-1", ParentMessageId = "msg-1" },
            new SubagentFinishedEvent { SubagentRunId = "northwind-supply-3f9a1c2e" },
            new ToolCallResultEvent { MessageId = "call-1", ToolCallId = "call-1", Content = """{"canSupply":true}""" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" });

        var lane = Assert.Single(Assert.Single(SpecialistBoard.From(view).Runs).Lanes);
        Assert.Equal(("Northwind supply", "check_supplier_stock", LaneStatus.Done), (lane.Label, lane.SpawnedBy, lane.Status));
        Assert.Empty(lane.Entries); // the call belongs to the agent that made it, not to the lane
    }

    [Fact]
    public void Attribution_without_lifecycle_still_groups_into_a_lane()
    {
        var view = Views.Fold(
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new TextMessageStartEvent { MessageId = "m-1", Role = AGUIRoles.Assistant, SubagentRunId = "researcher-7" },
            new TextMessageContentEvent { MessageId = "m-1", Delta = "Found three manuals.", SubagentRunId = "researcher-7" },
            new TextMessageEndEvent { MessageId = "m-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" });

        var lane = Assert.Single(Assert.Single(SpecialistBoard.From(view).Runs).Lanes);
        Assert.Equal(("researcher-7", LaneStatus.Unannounced), (lane.Name, lane.Status));
        Assert.Equal("Found three manuals.", Assert.IsType<LaneMessage>(Assert.Single(lane.Entries)).Text);
    }

    [Fact]
    public void A_later_runs_SUBAGENT_STARTED_for_a_suspended_id_continues_the_same_lane()
    {
        var suspended = Views.Fold(
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" },
            new SubagentFinishedEvent { SubagentRunId = "parts-1", Outcome = new SubagentFinishedSuspendedOutcome { InterruptIds = ["apr_1"] } },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" });
        var continued = Views.Fold(suspended,
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-2" },
            new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" });
        var done = Views.Fold(continued, new SubagentFinishedEvent { SubagentRunId = "parts-1" });

        // One lane throughout, never a duplicate: suspended, back to work, then done.
        Assert.Equal(LaneStatus.Suspended, Assert.Single(Assert.Single(SpecialistBoard.From(suspended).Runs).Lanes).Status);
        Assert.Equal(LaneStatus.Running, Assert.Single(Assert.Single(SpecialistBoard.From(continued).Runs).Lanes).Status);
        Assert.Equal(LaneStatus.Done, Assert.Single(Assert.Single(SpecialistBoard.From(done).Runs).Lanes).Status);
    }

    [Fact]
    public void A_delegated_invocation_nests_under_the_one_that_started_it()
    {
        var view = Views.Fold(
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" },
            new SubagentStartedEvent { SubagentRunId = "parts-1", Name = "parts-specialist" },
            new SubagentStartedEvent { SubagentRunId = "supplier-1", Name = "supplier-check", ParentSubagentRunId = "parts-1" },
            new SubagentErrorEvent { SubagentRunId = "supplier-1", Message = "The supplier didn't answer.", Code = "timeout" },
            new SubagentFinishedEvent { SubagentRunId = "parts-1" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-1" });

        var parts = Assert.Single(Assert.Single(SpecialistBoard.From(view).Runs).Lanes);
        var child = Assert.Single(parts.Children);
        Assert.Equal((LaneStatus.Done, LaneStatus.Failed, "The supplier didn't answer."), (parts.Status, child.Status, child.Error));
    }
}
