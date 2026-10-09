using System.Text.Json.Nodes;
using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 3: patches computed from a before and an after document.</summary>
public sealed class DiffTests
{
    // What the agent's work did to the sheet: C3 ticked, a note added, the job under way.
    private static JsonNode After()
    {
        var sheet = Sheets.Parse();
        sheet["workOrder"]!["status"] = "InProgress";
        sheet["workOrder"]!["checklist"]![2]!["done"] = true;
        sheet["workOrder"]!["notes"]!.AsArray().Add("Run capacitor reads 15.6% below its rating.");
        return sheet;
    }

    [Fact]
    public void A_diff_lists_each_change_in_document_order()
    {
        var patch = JsonDiff.Create(Sheets.Parse(), After());
        Capture.Write("diff", patch);

        var operations = PatchOps.ToElement(patch);
        Assert.Equal(3, operations.GetArrayLength());
        Sheets.AssertOp(operations[0], "replace", "/workOrder/status", "\"InProgress\"");
        Sheets.AssertOp(operations[1], "replace", "/workOrder/checklist/2/done", "true");
        Sheets.AssertOp(operations[2], "add", "/workOrder/notes/-", "\"Run capacitor reads 15.6% below its rating.\"");
        Assert.True(JsonNode.DeepEquals(After(), Sheets.Applied(Sheets.Parse(), patch)));
    }

    [Fact]
    public void A_guarded_diff_tests_every_value_it_overwrites()
    {
        var patch = JsonDiff.Create(Sheets.Parse(), After(), guard: true);
        Capture.Write("diff-guarded", patch);

        var operations = PatchOps.ToElement(patch);
        Assert.Equal(5, operations.GetArrayLength());
        Sheets.AssertOp(operations[0], "test", "/workOrder/status", "\"Scheduled\"");
        Sheets.AssertOp(operations[1], "replace", "/workOrder/status", "\"InProgress\"");
        Sheets.AssertOp(operations[2], "test", "/workOrder/checklist/2/done", "false");
        Sheets.AssertOp(operations[3], "replace", "/workOrder/checklist/2/done", "true");
        Sheets.AssertOp(operations[4], "add", "/workOrder/notes/-", "\"Run capacitor reads 15.6% below its rating.\"");
    }

    [Fact]
    public void Only_the_guarded_diff_refuses_to_overwrite_a_concurrent_edit()
    {
        // Dana put the job on hold while the agent worked.
        var dispatcher = Sheets.Parse();
        dispatcher["workOrder"]!["status"] = "OnHold";

        var unguarded = JsonPatch.Apply(dispatcher, PatchOps.ToElement(JsonDiff.Create(Sheets.Parse(), After())));
        var guarded = JsonPatch.Apply(dispatcher, PatchOps.ToElement(JsonDiff.Create(Sheets.Parse(), After(), guard: true)));

        Assert.True(unguarded.Succeeded);
        Assert.Equal("\"InProgress\"", Sheets.At(unguarded.Document, "/workOrder/status")); // her edit, silently gone
        Assert.False(guarded.Succeeded);
        Assert.Same(dispatcher, guarded.Document);
    }

    [Fact]
    public void A_diff_is_positional_so_an_insertion_at_the_top_rewrites_every_item()
    {
        var after = Sheets.Parse();
        after["workOrder"]!["checklist"]!.AsArray().Insert(0, new JsonObject { ["id"] = "D1", ["text"] = "Call ahead", ["done"] = false });

        var diff = JsonDiff.Create(Sheets.Parse(), after);
        JsonArray intent = [PatchOps.Add("/workOrder/checklist/0", after["workOrder"]!["checklist"]![0])];

        // Four items each get a new id and text, then the old last item is re-added at the end: nine operations.
        Assert.Equal(9, diff.Count);
        Assert.True(JsonNode.DeepEquals(after, Sheets.Applied(Sheets.Parse(), diff)));
        Assert.True(JsonNode.DeepEquals(after, Sheets.Applied(Sheets.Parse(), intent)));
    }

    [Fact]
    public void Removed_members_and_shortened_arrays_come_out_as_removes_from_the_end()
    {
        var after = Sheets.Parse();
        after["workOrder"]!.AsObject().Remove("readings");
        after["workOrder"]!["checklist"]!.AsArray().RemoveAt(3);
        after["workOrder"]!["checklist"]!.AsArray().RemoveAt(2);

        var operations = PatchOps.ToElement(JsonDiff.Create(Sheets.Parse(), after));

        Sheets.AssertOp(operations[0], "remove", "/workOrder/readings");
        Sheets.AssertOp(operations[1], "remove", "/workOrder/checklist/3");
        Sheets.AssertOp(operations[2], "remove", "/workOrder/checklist/2");
        Assert.True(JsonNode.DeepEquals(after, JsonPatch.Apply(Sheets.Parse(), operations).Document));
    }

    [Fact]
    public void Nothing_changed_is_an_empty_patch()
    {
        Assert.Empty(JsonDiff.Create(Sheets.Parse(), Sheets.Parse(), guard: true));
    }
}
