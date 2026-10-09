using Beacon.Client.Core;
using Ch18.Beacon.Desk.Activity;
using Ch18.Tests.Infrastructure;

namespace Ch18.Tests;

/// <summary>Beacon Desk's client stack against the agent: the Subagents slice, attribution and the board built from them.</summary>
public sealed class DeskClientTests
{
    [Fact]
    public async Task Every_message_and_tool_call_arrives_attributed_and_the_board_has_a_lane_per_invocation()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);

        await desk.SendAsync(Runs.Capacitor);
        var view = desk.Conversation.View;

        Assert.Equal(ConversationStatus.Idle, view.Status);
        Assert.Empty(view.Warnings);
        var invocations = view.Subagents.Select(s => s.SubagentRunId).ToHashSet();
        Assert.Equal(3, invocations.Count);
        Assert.All(view.Subagents, s => Assert.Equal(SubagentStatus.Succeeded, s.Status));

        // Each specialist's messages and calls carry its invocation's ID; the coordinator's message carries none.
        var assistant = view.Messages.OfType<AssistantMessageView>().ToList();
        Assert.Single(assistant, m => m.SubagentRunId is null);
        Assert.All(assistant.Where(m => m.SubagentRunId is not null), m => Assert.Contains(m.SubagentRunId!, invocations));
        Assert.Equal(3, view.ToolCalls.Count);
        Assert.All(view.ToolCalls, c => Assert.Contains(c.SubagentRunId!, invocations));
        Assert.All(view.ToolCalls, c => Assert.Equal(ToolCallStatus.Completed, c.Status));

        var board = SpecialistBoard.From(view);
        var run = Assert.Single(board.Runs);
        Assert.Equal(["Parts specialist", "Scheduling specialist", "Warranty specialist"], run.Lanes.Select(l => l.Label).Order());
        Assert.All(run.Lanes, lane =>
        {
            Assert.Equal(LaneStatus.Done, lane.Status);
            Assert.Contains(lane.Entries, e => e is LaneToolCall);
            Assert.All(lane.Entries, e => Assert.Equal(lane.Label, e.Owner));
        });
        Assert.Equal(["Planning", "Consulting", "Answering"], board.Steps.Select(s => s.Label));
    }

    [Fact]
    public async Task Approving_from_the_lane_resumes_the_parts_specialist_under_the_same_id()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);

        await desk.SendAsync(Runs.Compressor);

        var view = desk.Conversation.View;
        Assert.Equal(ConversationStatus.AwaitingInput, view.Status);
        var parts = SpecialistBoard.From(view).Runs.Single().Lanes.Single(l => l.Name == "parts-specialist");
        Assert.Equal(LaneStatus.WaitingForApproval, parts.Status);
        var interrupt = Assert.Single(parts.OpenInterrupts);
        Assert.Equal(parts.SubagentRunId, interrupt.SubagentRunId);
        Assert.Equal(ToolCallStatus.AwaitingApproval, view.FindToolCall(interrupt.ToolCallId!)!.Status);

        await LaneApprovals.ApproveAsync(desk.Conversation, interrupt, Runs.Ct);

        view = desk.Conversation.View;
        Assert.Equal(ConversationStatus.Idle, view.Status);
        var order = Assert.Single(host.Store.ListPartOrders());

        // One lane for the whole story: the continuation reused the ID, so the order lands in the same lane.
        var lane = Assert.Single(SpecialistBoard.From(view).Runs.SelectMany(r => r.Lanes), l => l.Name == "parts-specialist");
        Assert.Equal(LaneStatus.Done, lane.Status);
        var call = Assert.Single(lane.Entries.OfType<LaneToolCall>(), e => e.Call.Name == "place_part_order").Call;
        Assert.Equal(ToolCallStatus.Completed, call.Status);
        Assert.Contains(order.Id, call.Result, StringComparison.Ordinal);

        // The lane's story, from the views the Desk rendered: waiting, Resuming once the agent accepted the answer,
        // back to work when the continuation's SUBAGENT_STARTED arrived under the same ID, then done.
        List<LaneStatus> story = [];
        foreach (var status in desk.Views.Select(v => SpecialistBoard.From(v).Runs.SelectMany(r => r.Lanes).SingleOrDefault(l => l.Name == "parts-specialist")?.Status))
        {
            if (status is { } s && (story.Count == 0 || story[^1] != s))
            {
                story.Add(s);
            }
        }

        Assert.Equal([LaneStatus.WaitingForApproval, LaneStatus.Resuming, LaneStatus.Running, LaneStatus.Done], story.SkipWhile(s => s != LaneStatus.WaitingForApproval));
    }

    [Fact]
    public async Task An_approval_another_Desk_answered_first_is_refused_and_stays_open()
    {
        await using var host = new AgentHost();
        using var desk = new DeskStack(host);
        await desk.SendAsync(Runs.Compressor);
        var interrupt = desk.Conversation.View.OpenInterrupts.Single();

        // Another Desk on the same thread answers first; the agent spends the approval once.
        var other = await Runs.RawAsync(host.Client(), Runs.Resume("t-desk", "r-other", Runs.Compressor, Runs.SdkApproval(interrupt.Id, approved: true, interrupt.ToolCallId!)));
        Assert.Equal("RUN_FINISHED", other[^1].Type());

        await LaneApprovals.ApproveAsync(desk.Conversation, interrupt, Runs.Ct);

        // The resume gate's 409 before any event: refused, nothing ran twice, and the approval is still open here.
        var view = desk.Conversation.View;
        Assert.Equal((FailureKind.Rejected, System.Net.HttpStatusCode.Conflict, true), (view.Failure!.Kind, view.Failure.StatusCode, view.Failure.RunNeverStarted));
        Assert.Single(host.Store.ListPartOrders());
        Assert.Equal(FailureKind.Rejected, Assert.Single(view.OpenInterrupts).LastAnswerFailure!.Kind);
    }

    [Fact]
    public async Task Without_the_result_aligned_transport_a_slow_lookup_holds_back_the_other_lanes()
    {
        // The parts lookup takes a moment; scheduling and warranty answer at once.
        Assert.True(await SchedulingFinishedWhilePartsSearchedAsync(aligned: true));
        Assert.False(await SchedulingFinishedWhilePartsSearchedAsync(aligned: false));
    }

    // Did the Desk show the scheduling lane done while the parts specialist's lookup was still running? The
    // scheduling lookup waits until the parts lookup has started, so its lane can only finish during it.
    private static async Task<bool> SchedulingFinishedWhilePartsSearchedAsync(bool aligned)
    {
        using var partsStarted = new ManualResetEventSlim();
        var partsReturned = false;
        await using var host = new AgentHost(new TestStore
        {
            BeforeSearchParts = () =>
            {
                partsStarted.Set();
                Thread.Sleep(TimeSpan.FromMilliseconds(400));
                Volatile.Write(ref partsReturned, true);
            },
            BeforeGetSchedule = () => partsStarted.Wait(TimeSpan.FromSeconds(5)),
        });
        using var desk = new DeskStack(host, aligned);

        var shownWhileSearching = false;
        desk.Conversation.Changed += (_, e) =>
        {
            if (!Volatile.Read(ref partsReturned)
                && SpecialistBoard.From(e.View).Runs.SelectMany(r => r.Lanes).Any(l => l.Name == "scheduling-specialist" && l.Status == LaneStatus.Done))
            {
                shownWhileSearching = true;
            }
        };

        await desk.SendAsync(Runs.Capacitor);
        Assert.All(desk.Conversation.View.Subagents, s => Assert.Equal(SubagentStatus.Succeeded, s.Status));
        return shownWhileSearching;
    }
}
