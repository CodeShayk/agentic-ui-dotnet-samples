using System.Text.Json.Nodes;
using AGUI.Abstractions;
using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 6: choosing between STATE_SNAPSHOT and STATE_DELTA.</summary>
public sealed class SnapshotOrDeltaTests
{
    [Fact]
    public void Nothing_changed_sends_nothing()
    {
        Assert.Null(StateUpdates.Next(Sheets.Parse(), Sheets.Parse(), baselineKnown: true));
    }

    [Fact]
    public void A_small_change_on_a_known_baseline_is_a_guarded_delta()
    {
        var after = Sheets.Parse();
        after["workOrder"]!["checklist"]![2]!["done"] = true;

        var evt = Assert.IsType<StateDeltaEvent>(StateUpdates.Next(Sheets.Parse(), after, baselineKnown: true));
        Capture.Write("next-delta", evt.Delta);

        // The sizes the recipes quote: the guarded tick against the whole ticked sheet, both compact.
        var deltaBytes = evt.Delta.GetRawText().Length;
        var snapshotBytes = after.ToJsonString().Length;
        Capture.Write("tick-sizes", $$"""{"snapshotBytes":{{snapshotBytes}},"deltaBytes":{{deltaBytes}}}""");
        Assert.True(deltaBytes * 2 <= snapshotBytes, $"{deltaBytes} bytes of delta against {snapshotBytes} of snapshot");

        Sheets.AssertOp(evt.Delta[0], "test", "/workOrder/checklist/2/done", "false");
        Sheets.AssertOp(evt.Delta[1], "replace", "/workOrder/checklist/2/done", "true");
        Assert.True(JsonNode.DeepEquals(after, JsonPatch.Apply(Sheets.Parse(), evt.Delta).Document));
    }

    [Fact]
    public void A_positional_rewrite_costs_more_than_the_document_so_it_goes_as_a_snapshot()
    {
        var after = Sheets.Parse();
        after["workOrder"]!["checklist"]!.AsArray().Insert(0, new JsonObject { ["id"] = "D1", ["text"] = "Call ahead", ["done"] = false });

        var evt = StateUpdates.Next(Sheets.Parse(), after, baselineKnown: true);
        var guarded = PatchOps.ToElement(JsonDiff.Create(Sheets.Parse(), after, guard: true));
        Capture.Write("sizes", $"{{\"snapshotBytes\":{after.ToJsonString().Length},\"guardedDiffBytes\":{guarded.GetRawText().Length},\"operations\":{guarded.GetArrayLength()}}}");

        var snapshot = Assert.IsType<StateSnapshotEvent>(evt);
        Assert.True(JsonNode.DeepEquals(after, JsonNode.Parse(snapshot.Snapshot.GetRawText())));
    }

    [Fact]
    public void An_unknown_baseline_or_a_change_of_kind_is_always_a_snapshot()
    {
        var after = Sheets.Parse();
        after["workOrder"]!["status"] = "InProgress";

        Assert.IsType<StateSnapshotEvent>(StateUpdates.Next(Sheets.Parse(), after, baselineKnown: false));
        Assert.IsType<StateSnapshotEvent>(StateUpdates.Next(null, after, baselineKnown: true));
        Assert.IsType<StateSnapshotEvent>(StateUpdates.Next(new JsonArray(), after, baselineKnown: true));
    }
}
