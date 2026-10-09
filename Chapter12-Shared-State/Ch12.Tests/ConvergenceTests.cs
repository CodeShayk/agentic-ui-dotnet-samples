using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Ch12.Beacon.Desk.Sheet;

namespace Ch12.Tests;

/// <summary>
/// The dispatcher and the agent edit the sheet at the same time, through Beacon Desk's real client stack
/// and editor against the real agent. The dispatcher's edit happens on the stream's thread when a chosen
/// event arrives, before the next one is applied, so every interleaving is exact and repeatable.
/// </summary>
public sealed class ConvergenceTests
{
    private const string Reading = "Priya measured the run capacitor on WO-1001: it reads 38 µF against its 45 µF rating, 15.6% low.";

    [Fact]
    public async Task A_removal_during_the_run_makes_a_delta_fail_and_the_settling_snapshot_converges_both_edits()
    {
        await using var agent = new AgentHost();
        var recorder = new RequestRecorder();
        using var desk = new DeskStack(agent.CreateClient(recorder));
        await desk.SendAsync("Open WO-1001");

        // Dana deletes C1 just after the run started: the agent's state is the sheet with C1 in it.
        desk.EditWhen(e => e.Action is UpdateReceived { Update.RawRepresentation: RunStartedEvent } && e.View.Runs.Count == 2,
            () => Assert.True(desk.Editor.Apply(new RemoveItem("C1"))));
        await desk.SendAsync(Reading);

        // The agent's first delta pinned C3 at index 2; on Dana's copy that's C4, so the patch was refused.
        Assert.Contains(desk.Views, v => v.State is { NeedsResync: true, LastPatchError: { } error } && error.Contains("/workOrder/checklist/2/id", StringComparison.Ordinal));
        Assert.DoesNotContain(desk.Views, v => SheetDocument.IsDone(SheetDocument.Item(v.State.Document, "C4")));

        // The settling snapshot restored the agent's change and the editor put Dana's back on top.
        var state = desk.Conversation.View.State;
        Assert.False(state.NeedsResync);
        Assert.Equal(StateChangeSource.Client, state.LastChangedBy);
        var sheet = SheetDocument.Read(state.Document)!;
        Assert.Equal(["C2", "C3", "C4", "B1"], sheet.Checklist.Select(i => i.Id));
        Assert.True(sheet.Checklist.Single(i => i.Id == "C3").Done);
        Assert.Equal(2, sheet.Notes.Count);
        Assert.Empty(desk.Editor.Notices); // the agent never touched C1: nothing to report
        Assert.True(desk.Editor.Sync.HasUnsentEdits);

        // The next run carries the converged sheet back to the agent.
        await desk.SendAsync("What is the status of WO-1001?");
        Assert.True(JsonNode.DeepEquals(state.Document, recorder.LastState));
        Assert.False(desk.Editor.Sync.HasUnsentEdits);
    }

    [Fact]
    public async Task When_both_change_the_same_item_the_dispatcher_wins_and_is_told()
    {
        await using var agent = new AgentHost();
        using var desk = new DeskStack(agent.CreateClient());
        await desk.SendAsync("Open WO-1001");

        // As soon as Beacon's tick of C3 lands, Dana clears it: Priya hasn't inspected it yet.
        desk.EditWhen(e => e.Action is UpdateReceived { Update.RawRepresentation: StateDeltaEvent } && SheetDocument.IsDone(SheetDocument.Item(e.View.State.Document, "C3")),
            () => Assert.True(desk.Editor.Apply(new SetItemDone("C3", false))));
        await desk.SendAsync(Reading);

        var sheet = SheetDocument.Read(desk.Document)!;
        Assert.False(sheet.Checklist.Single(i => i.Id == "C3").Done);       // her edit survived the snapshot
        Assert.Contains(sheet.Checklist, i => i.Id == "B1");                // and Beacon's other changes stand
        var notice = Assert.Single(desk.Editor.Notices);
        Assert.Equal("Beacon also changed 'Inspect run capacitor'. Your edit was kept.", notice);
    }

    [Fact]
    public async Task Items_added_by_both_sides_never_collide_and_both_survive()
    {
        await using var agent = new AgentHost();
        using var desk = new DeskStack(agent.CreateClient());
        await desk.SendAsync("Open WO-1001");

        desk.EditWhen(e => e.Action is UpdateReceived { Update.RawRepresentation: RunStartedEvent } && e.View.Runs.Count == 2,
            () => Assert.True(desk.Editor.Apply(new AddItem(desk.Editor.NextItemId(), "Call the office before arriving"))));
        await desk.SendAsync(Reading);

        // While the run streamed the order was D1, B1; the snapshot put B1 first and the rebase appended D1.
        Assert.Contains(desk.Views, v => Ids(v) is [.., "D1", "B1"]);
        Assert.Equal(["C1", "C2", "C3", "C4", "B1", "D1"], Ids(desk.Conversation.View));
        Assert.Empty(desk.Editor.Notices);
    }

    [Fact]
    public async Task Edits_made_between_runs_go_out_with_the_next_run_and_stop_being_unsent()
    {
        await using var agent = new AgentHost();
        var recorder = new RequestRecorder();
        using var desk = new DeskStack(agent.CreateClient(recorder));
        await desk.SendAsync("Open WO-1001");
        Assert.False(desk.Editor.Sync.HasUnsentEdits);

        Assert.True(desk.Editor.Apply(new SetItemDone("C1", true)));
        Assert.True(desk.Editor.Apply(new SetStatus("InProgress")));
        Assert.False(desk.Editor.Apply(new SetStatus("InProgress"))); // already in effect: no change, no version
        Assert.True(desk.Editor.Sync.HasUnsentEdits);
        Assert.Equal(StateChangeSource.Client, desk.Editor.Sync.LastChangedBy);

        await desk.SendAsync("What is the status of WO-1001?");

        var sent = recorder.LastState!;
        Assert.Equal("InProgress", sent["workOrder"]!["status"]!.GetValue<string>());
        Assert.True(sent["workOrder"]!["checklist"]![0]!["done"]!.GetValue<bool>());
        Assert.False(desk.Editor.Sync.HasUnsentEdits);
    }

    [Fact]
    public async Task The_board_day_travels_as_context_on_every_run()
    {
        await using var agent = new AgentHost();
        var recorder = new RequestRecorder();
        using var desk = new DeskStack(agent.CreateClient(recorder));
        await desk.SendAsync("Open WO-1001");

        desk.Board.SelectedDate = new DateOnly(2026, 11, 18);
        await desk.SendAsync("Add a return visit on the day selected on my board.");

        var context = recorder.Bodies[^1].GetProperty("context");
        Assert.Equal("2026-11-18", context[0].GetProperty("value").GetString());
        Assert.Contains(SheetDocument.Read(desk.Document)!.Checklist, i => i.Text == "Return visit on Wednesday, November 18 to confirm the repair");
    }

    private static string[] Ids(ConversationView view) =>
        [.. SheetDocument.Read(view.State.Document)?.Checklist.Select(i => i.Id) ?? []];
}
