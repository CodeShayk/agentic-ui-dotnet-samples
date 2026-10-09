using System.Text.Json.Nodes;
using AGUI.Abstractions;
using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 5: applying deltas on the client, keeping the three kinds of failure apart.</summary>
public sealed class ReplicaTests
{
    private static StateSnapshotEvent Snapshot() => new() { Snapshot = Sheets.Json(Sheets.WorkOrder1001) };

    private static StateDeltaEvent Delta(string patch) => new() { Delta = Sheets.Json(patch) };

    [Fact]
    public void A_delta_that_does_not_apply_is_skipped_and_the_next_snapshot_resynchronizes()
    {
        var replica = new StateReplica();
        replica.BeginRun(inputState: null);
        Assert.Equal(StateOutcome.Replaced, replica.Apply(Snapshot()));

        // Pinned to C3 at index 3, where C4 actually is: a well-formed patch that doesn't apply.
        var before = replica.Document;
        var outcome = replica.Apply(Delta("""[{"op":"test","path":"/workOrder/checklist/3/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/3/done","value":true}]"""));

        Assert.Equal(StateOutcome.Skipped, outcome);
        Assert.True(replica.NeedsResync);
        Assert.Same(before, replica.Document);
        Assert.Contains(replica.Warnings, w => w.Contains("the test at '/workOrder/checklist/3/id' failed", StringComparison.Ordinal));

        // Later deltas still apply to the kept value; only a snapshot clears the flag.
        Assert.Equal(StateOutcome.Patched, replica.Apply(Delta("""[{"op":"add","path":"/workOrder/notes/-","value":"Gate code on file."}]""")));
        Assert.True(replica.NeedsResync);
        Assert.Equal(StateOutcome.Replaced, replica.Apply(Snapshot()));
        Assert.False(replica.NeedsResync);
    }

    [Fact]
    public void A_patch_is_all_or_nothing()
    {
        var replica = new StateReplica();
        replica.Apply(Snapshot());
        var before = replica.Document;

        // The first two operations would apply on their own; the third names an item that isn't there.
        var outcome = replica.Apply(Delta("""
            [{"op":"replace","path":"/workOrder/status","value":"InProgress"},
             {"op":"add","path":"/workOrder/notes/-","value":"Started."},
             {"op":"remove","path":"/workOrder/checklist/9"}]
            """));

        Assert.Equal(StateOutcome.Skipped, outcome);
        Assert.Same(before, replica.Document);
        Assert.Equal("\"Scheduled\"", Sheets.At(replica.Document, "/workOrder/status"));
    }

    [Theory]
    [InlineData("""{"op":"replace","path":"/workOrder/status","value":"InProgress"}""")]   // not an array
    [InlineData("""[{"path":"/workOrder/status","value":"InProgress"}]""")]                 // no op
    [InlineData("""[{"op":"replace","path":"/workOrder/status"}]""")]                       // replace without value
    [InlineData("""[{"op":"move","path":"/workOrder/checklist/0"}]""")]                     // move without from
    [InlineData("""[{"op":"remove","path":"workOrder/status"}]""")]                         // not a JSON Pointer
    public void A_malformed_delta_is_reported_as_fatal_and_changes_nothing(string patch)
    {
        var replica = new StateReplica();
        replica.Apply(Snapshot());
        var before = replica.Document;

        Assert.Equal(StateOutcome.Malformed, replica.Apply(Delta(patch)));
        Assert.Same(before, replica.Document);
    }

    [Fact]
    public void An_unrecognized_op_is_dropped_with_a_warning_and_the_rest_applies()
    {
        var replica = new StateReplica();
        replica.Apply(Snapshot());

        var outcome = replica.Apply(Delta("""[{"op":"merge","path":"/workOrder","value":{}},{"op":"replace","path":"/workOrder/status","value":"InProgress"}]"""));

        Assert.Equal(StateOutcome.Patched, outcome);
        Assert.Single(replica.Warnings);
        Assert.Equal("\"InProgress\"", Sheets.At(replica.Document, "/workOrder/status"));
    }

    [Fact]
    public void A_run_starts_from_its_input_state_or_from_an_empty_object()
    {
        var replica = new StateReplica();

        replica.BeginRun(inputState: null);
        replica.Apply(Delta("""[{"op":"add","path":"/schemaVersion","value":2}]"""));
        Assert.Equal("""{"schemaVersion":2}""", replica.Document!.ToJsonString());

        replica.BeginRun(Sheets.Json(Sheets.WorkOrder1001));
        replica.Apply(new StateDeltaEvent { Delta = PatchOps.ToElement(ChecklistPatches.SetDone(Sheets.Parse(), "C3", done: true)!) });
        Assert.Equal("true", Sheets.At(replica.Document, "/workOrder/checklist/2/done"));
    }

    [Fact]
    public void A_null_value_is_data_and_survives_the_round_trip()
    {
        var replica = new StateReplica();
        replica.Apply(Snapshot());

        replica.Apply(new StateDeltaEvent { Delta = PatchOps.ToElement([PatchOps.Add("/workOrder/eta", null)]) });

        Assert.Equal("null", Sheets.At(replica.Document, "/workOrder/eta"));
        Assert.True(replica.Document!["workOrder"]!.AsObject().ContainsKey("eta"));
        Assert.IsType<JsonObject>(replica.Document);
    }
}
