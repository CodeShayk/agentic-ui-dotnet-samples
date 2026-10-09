using System.Text.Json.Nodes;
using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 2: patches built from intent, each pinned to the identity of what it touches.</summary>
public sealed class IntentPatchTests
{
    private static readonly JsonObject NewItem = new() { ["id"] = "B1", ["text"] = "Replace run capacitor (CAP-45-5)", ["done"] = false };

    [Fact]
    public void Add_appends_with_a_dash_whatever_the_client_holds()
    {
        var patch = ChecklistPatches.Add(NewItem);
        Capture.Write("add", patch);

        // Dana removed C1 while the agent worked. "-" still means "after the last item".
        var dispatcher = Sheets.Applied(Sheets.Parse(), ChecklistPatches.Remove(Sheets.Parse(), "C1"));
        var sheet = Sheets.Applied(dispatcher, patch);

        Assert.Equal(["C2", "C3", "C4", "B1"], Sheets.Ids(sheet));
    }

    [Fact]
    public void SetDone_tests_the_item_id_before_it_replaces_done()
    {
        var patch = PatchOps.ToElement(ChecklistPatches.SetDone(Sheets.Parse(), "C3", done: true)!);
        Capture.Write("set-done", patch);

        Assert.Equal(2, patch.GetArrayLength());
        Sheets.AssertOp(patch[0], "test", "/workOrder/checklist/2/id", "\"C3\"");
        Sheets.AssertOp(patch[1], "replace", "/workOrder/checklist/2/done", "true");
    }

    [Fact]
    public void SetDone_on_a_shifted_list_fails_instead_of_ticking_the_wrong_item()
    {
        var patch = PatchOps.ToElement(ChecklistPatches.SetDone(Sheets.Parse(), "C3", done: true)!);
        var dispatcher = Sheets.Applied(Sheets.Parse(), ChecklistPatches.Remove(Sheets.Parse(), "C1"));

        var result = JsonPatch.Apply(dispatcher, patch);

        Assert.False(result.Succeeded);
        Assert.Same(dispatcher, result.Document);
        Assert.Contains("/workOrder/checklist/2/id", result.Error);
    }

    [Fact]
    public void SetDone_returns_an_empty_patch_when_there_is_nothing_to_do_and_null_when_there_is_no_such_item()
    {
        var ticked = Sheets.Applied(Sheets.Parse(), ChecklistPatches.SetDone(Sheets.Parse(), "C3", done: true)!);

        Assert.Empty(ChecklistPatches.SetDone(ticked, "C3", done: true)!);
        Assert.Null(ChecklistPatches.SetDone(ticked, "C9", done: true));
    }

    [Fact]
    public void MoveAfter_pins_both_items_and_indexes_the_list_without_the_moved_item()
    {
        var patch = ChecklistPatches.MoveAfter(Sheets.Parse(), "C1", afterItemId: "C3")!;
        Capture.Write("move", patch);

        var operations = PatchOps.ToElement(patch);
        Sheets.AssertOp(operations[0], "test", "/workOrder/checklist/0/id", "\"C1\"");
        Sheets.AssertOp(operations[1], "test", "/workOrder/checklist/2/id", "\"C3\"");
        Sheets.AssertOp(operations[2], "move", "/workOrder/checklist/2", from: "/workOrder/checklist/0");
        Assert.Equal(["C2", "C3", "C1", "C4"], Sheets.Ids(Sheets.Applied(Sheets.Parse(), patch)));
    }

    [Fact]
    public void A_move_computed_against_the_list_before_removal_lands_one_place_late()
    {
        // "After C3" is index 3 in the list as it stands, but a move removes C1 first, so index 3 is now the end.
        var naive = new JsonArray(PatchOps.Move("/workOrder/checklist/0", "/workOrder/checklist/3"));

        Assert.Equal(["C2", "C3", "C4", "C1"], Sheets.Ids(Sheets.Applied(Sheets.Parse(), naive)));
    }

    [Fact]
    public void MoveAfter_to_the_top_and_downward_both_keep_the_anchor_semantics()
    {
        Assert.Equal(["C4", "C1", "C2", "C3"], Sheets.Ids(Sheets.Applied(Sheets.Parse(), ChecklistPatches.MoveAfter(Sheets.Parse(), "C4", afterItemId: null)!)));
        Assert.Equal(["C1", "C4", "C2", "C3"], Sheets.Ids(Sheets.Applied(Sheets.Parse(), ChecklistPatches.MoveAfter(Sheets.Parse(), "C4", afterItemId: "C1")!)));
        Assert.Empty(ChecklistPatches.MoveAfter(Sheets.Parse(), "C2", afterItemId: "C1")!); // already there
    }

    [Fact]
    public void MoveAfter_fails_when_someone_else_moved_the_anchor()
    {
        var patch = PatchOps.ToElement(ChecklistPatches.MoveAfter(Sheets.Parse(), "C1", afterItemId: "C3")!);

        // Dana dragged C3 to the top first. C1 is where the agent thought it was; C3 isn't.
        var dispatcher = Sheets.Applied(Sheets.Parse(), ChecklistPatches.MoveAfter(Sheets.Parse(), "C3", afterItemId: null)!);
        var result = JsonPatch.Apply(dispatcher, patch);

        Assert.False(result.Succeeded);
        Assert.Same(dispatcher, result.Document);
    }

    [Fact]
    public void Remove_pins_the_item_and_is_empty_when_it_is_already_gone()
    {
        var patch = PatchOps.ToElement(ChecklistPatches.Remove(Sheets.Parse(), "C2"));

        Sheets.AssertOp(patch[0], "test", "/workOrder/checklist/1/id", "\"C2\"");
        Sheets.AssertOp(patch[1], "remove", "/workOrder/checklist/1");

        var removed = JsonPatch.Apply(Sheets.Parse(), patch).Document;
        Assert.Equal(["C1", "C3", "C4"], Sheets.Ids(removed));
        Assert.Empty(ChecklistPatches.Remove(removed, "C2"));
    }
}
