using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch12.Tests;

/// <summary>
/// RFC 6902 as Beacon Desk applies it to the sheet: each operation, appends with "-", escaped pointers,
/// atomic failure, and what a failed STATE_DELTA does to the conversation's state (resync, not corruption).
/// </summary>
public sealed class JsonPatchEdgeCaseTests
{
    private const string Sheet = """
        {"schemaVersion":2,"workOrder":{"id":"WO-1001","status":"Scheduled","checklist":[
          {"id":"C1","text":"Check thermostat settings and wiring","done":false},
          {"id":"C2","text":"Measure refrigerant pressures","done":false},
          {"id":"C3","text":"Inspect run capacitor","done":false},
          {"id":"C4","text":"Test compressor amperage","done":false}],
          "notes":["Customer reports the unit stops after a few minutes."],
          "labels":{"a/b":1,"m~n":2,"~1":3}}}
        """;

    [Theory]
    [InlineData("""[{"op":"add","path":"/workOrder/checklist/-","value":{"id":"B1","text":"Replace run capacitor","done":false}}]""", "/workOrder/checklist/4/id", "\"B1\"")]
    [InlineData("""[{"op":"add","path":"/workOrder/checklist/0","value":{"id":"D1","text":"Call ahead","done":false}}]""", "/workOrder/checklist/1/id", "\"C1\"")]
    [InlineData("""[{"op":"add","path":"/workOrder/checklist/4","value":{"id":"D1"}}]""", "/workOrder/checklist/4/id", "\"D1\"")]           // index == length appends
    [InlineData("""[{"op":"add","path":"/workOrder/status","value":"InProgress"}]""", "/workOrder/status", "\"InProgress\"")]               // add replaces a member
    [InlineData("""[{"op":"remove","path":"/workOrder/checklist/0"}]""", "/workOrder/checklist/0/id", "\"C2\"")]                             // later indexes shift
    [InlineData("""[{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]""", "/workOrder/checklist/2/done", "true")]
    [InlineData("""[{"op":"move","from":"/workOrder/checklist/3","path":"/workOrder/checklist/0"}]""", "/workOrder/checklist/0/id", "\"C4\"")]
    [InlineData("""[{"op":"copy","from":"/workOrder/checklist/2/text","path":"/workOrder/notes/-"}]""", "/workOrder/notes/1", "\"Inspect run capacitor\"")]
    [InlineData("""[{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]""", "/workOrder/checklist/2/done", "true")]
    [InlineData("""[{"op":"test","path":"/workOrder/checklist/0","value":{"done":false,"text":"Check thermostat settings and wiring","id":"C1"}}]""", "/workOrder/checklist/0/id", "\"C1\"")] // member order doesn't matter
    [InlineData("""[{"op":"replace","path":"/workOrder/labels/a~1b","value":10}]""", "/workOrder/labels/a~1b", "10")]                        // ~1 is '/'
    [InlineData("""[{"op":"replace","path":"/workOrder/labels/m~0n","value":20}]""", "/workOrder/labels/m~0n", "20")]                        // ~0 is '~'
    [InlineData("""[{"op":"replace","path":"/workOrder/labels/~01","value":30}]""", "/workOrder/labels/~01", "30")]                          // ~01 is "~1", not "/"
    [InlineData("""[{"op":"replace","path":"","value":{"schemaVersion":2}}]""", "/schemaVersion", "2")]                                       // "" is the whole document
    public void Each_operation_applies_to_the_sheet(string patch, string pointer, string expected)
    {
        var result = JsonPatch.Apply(JsonNode.Parse(Sheet), Runs.Json(patch));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, At(result.Document, pointer));
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"/workOrder/checklist/-","value":{}}]""")]   // "-" names no element to replace
    [InlineData("""[{"op":"remove","path":"/workOrder/checklist/4"}]""")]               // past the end
    [InlineData("""[{"op":"add","path":"/workOrder/checklist/5","value":{}}]""")]       // beyond length
    [InlineData("""[{"op":"replace","path":"/workOrder/checklist/02/done","value":true}]""")] // leading zero isn't an index
    [InlineData("""[{"op":"replace","path":"/workOrder/assignee","value":"TECH-2"}]""")] // replace needs an existing target
    [InlineData("""[{"op":"test","path":"/workOrder/checklist/2/id","value":"C4"}]""")]  // a failed test fails the patch
    [InlineData("""[{"op":"move","from":"/workOrder","path":"/workOrder/copy"}]""")]     // into its own child
    [InlineData("""[{"op":"copy","from":"/workOrder/missing","path":"/x"}]""")]          // from must exist
    public void A_patch_that_does_not_apply_changes_nothing(string failing)
    {
        var document = JsonNode.Parse(Sheet);

        // The first operation would apply on its own. RFC 6902 is all or nothing.
        var patch = $$"""[{"op":"replace","path":"/workOrder/status","value":"InProgress"},{{failing[1..^1]}}]""";
        var result = JsonPatch.Apply(document, Runs.Json(patch));

        Assert.False(result.Succeeded);
        Assert.Same(document, result.Document);
        Assert.Equal("\"Scheduled\"", At(document, "/workOrder/status"));
    }

    [Fact]
    public void Without_the_test_operation_a_shifted_index_ticks_the_wrong_item()
    {
        // The dispatcher removed C1, so C3 moved from index 2 to index 1 on her copy.
        var dispatcher = JsonPatch.Apply(JsonNode.Parse(Sheet), Runs.Json("""[{"op":"remove","path":"/workOrder/checklist/0"}]""")).Document;

        // The agent computed its patch against the sheet as it was when the run started.
        var guarded = JsonPatch.Apply(dispatcher, Runs.Json("""[{"op":"test","path":"/workOrder/checklist/2/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]"""));
        var unguarded = JsonPatch.Apply(dispatcher, Runs.Json("""[{"op":"replace","path":"/workOrder/checklist/2/done","value":true}]"""));

        Assert.False(guarded.Succeeded);
        Assert.Contains("/workOrder/checklist/2/id", guarded.Error);
        Assert.True(unguarded.Succeeded);
        Assert.Equal("\"C4\"", At(unguarded.Document, "/workOrder/checklist/2/id")); // Test compressor amperage, ticked by mistake
        Assert.Equal("true", At(unguarded.Document, "/workOrder/checklist/2/done"));
    }

    [Fact]
    public void An_op_rfc_6902_does_not_define_is_skipped_with_a_warning_and_the_rest_applies()
    {
        var result = JsonPatch.Apply(JsonNode.Parse(Sheet), Runs.Json("""[{"op":"merge","path":"/workOrder","value":{}},{"op":"replace","path":"/workOrder/status","value":"InProgress"}]"""));

        Assert.True(result.Succeeded);
        Assert.Single(result.Warnings);
        Assert.Equal("\"InProgress\"", At(result.Document, "/workOrder/status"));
    }

    [Fact]
    public void A_delta_that_fails_flags_a_resync_and_the_next_snapshot_clears_it()
    {
        var view = ConversationView.Empty with { ThreadId = "t-1" };
        view = Receive(view, new StateSnapshotEvent { Snapshot = Runs.Json(Sheet) });
        var before = view.State;

        // C3 isn't at index 3: the test fails, so nothing in the patch applies.
        view = Receive(view, new StateDeltaEvent { Delta = Runs.Json("""[{"op":"test","path":"/workOrder/checklist/3/id","value":"C3"},{"op":"replace","path":"/workOrder/checklist/3/done","value":true}]""") });

        Assert.True(view.State.NeedsResync);
        Assert.Contains("the test at '/workOrder/checklist/3/id' failed", view.State.LastPatchError);
        Assert.Same(before.Document, view.State.Document);  // the prior value, untouched
        Assert.Equal(before.Version, view.State.Version);     // a skipped delta isn't a change
        Assert.Contains(view.Warnings, w => w.Contains("didn't apply", StringComparison.Ordinal));

        // A later delta that does apply still does, against the kept value; the flag stays until a snapshot.
        view = Receive(view, new StateDeltaEvent { Delta = Runs.Json("""[{"op":"add","path":"/workOrder/notes/-","value":"Gate code on file."}]""") });
        Assert.True(view.State.NeedsResync);
        Assert.Equal(before.Version + 1, view.State.Version);

        view = Receive(view, new StateSnapshotEvent { Snapshot = Runs.Json(Sheet) });
        Assert.False(view.State.NeedsResync);
        Assert.Null(view.State.LastPatchError);
        Assert.Equal(StateChangeSource.Agent, view.State.LastChangedBy);
    }

    [Fact]
    public void A_malformed_delta_fails_the_run_instead_of_flagging_a_resync()
    {
        var view = Receive(ConversationView.Empty with { ThreadId = "t-1" }, new RunStartedEvent { ThreadId = "t-1", RunId = "r-1" });
        view = Receive(view, new StateSnapshotEvent { Snapshot = Runs.Json(Sheet) });
        var before = view.State;

        // An operation with no path isn't JSON Patch at all. That is fatal, unlike a patch that doesn't apply.
        view = Receive(view, new StateDeltaEvent { Delta = Runs.Json("""[{"op":"replace","value":true}]""") });

        Assert.Equal(FailureKind.ProtocolViolation, view.Failure?.Kind);
        Assert.Equal(RunOutcome.Failed, view.CurrentRun!.Outcome);
        Assert.False(view.State.NeedsResync);
        Assert.Same(before.Document, view.State.Document);
    }

    [Fact]
    public void A_delta_before_any_state_patches_the_empty_object()
    {
        var view = Receive(ConversationView.Empty, new StateDeltaEvent { Delta = Runs.Json("""[{"op":"add","path":"/schemaVersion","value":2}]""") });

        Assert.Equal("""{"schemaVersion":2}""", view.State.Document!.ToJsonString());
    }

    private static ConversationView Receive(ConversationView view, BaseEvent evt) =>
        ConversationReducer.Reduce(view, new UpdateReceived(new ChatResponseUpdate { RawRepresentation = evt }));

    private static string At(JsonNode? document, string pointer)
    {
        var node = document;
        foreach (var segment in pointer.Split('/').Skip(1).Select(s => s.Replace("~1", "/").Replace("~0", "~")))
        {
            node = node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : node![segment];
        }

        return node?.ToJsonString() ?? "null";
    }
}
