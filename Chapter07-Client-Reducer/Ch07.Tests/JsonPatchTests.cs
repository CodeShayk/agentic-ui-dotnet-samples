using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Client.Core;

namespace Ch07.Tests;

/// <summary>RFC 6902 as the reducer applies it: every operation, pointer escapes, atomicity and the three outcomes (applied, not applied, malformed).</summary>
public sealed class JsonPatchTests
{
    private const string WorkOrder = """{"id":"WO-1001","checklist":[{"id":"C1","done":false},{"id":"C2","done":false}],"notes":{"a/b":1,"m~n":2}}""";

    [Theory]
    [InlineData("""[{"op":"add","path":"/priority","value":"High"}]""", "/priority", "\"High\"")]
    [InlineData("""[{"op":"add","path":"/checklist/-","value":{"id":"C3","done":false}}]""", "/checklist/2/id", "\"C3\"")]
    [InlineData("""[{"op":"add","path":"/checklist/0","value":{"id":"C0"}}]""", "/checklist/1/id", "\"C1\"")]
    [InlineData("""[{"op":"replace","path":"/checklist/1/done","value":true}]""", "/checklist/1/done", "true")]
    [InlineData("""[{"op":"remove","path":"/checklist/0"}]""", "/checklist/0/id", "\"C2\"")]
    [InlineData("""[{"op":"move","from":"/checklist/1","path":"/checklist/0"}]""", "/checklist/0/id", "\"C2\"")]
    [InlineData("""[{"op":"copy","from":"/id","path":"/workOrderId"}]""", "/workOrderId", "\"WO-1001\"")]
    [InlineData("""[{"op":"test","path":"/id","value":"WO-1001"},{"op":"add","path":"/ok","value":true}]""", "/ok", "true")]
    [InlineData("""[{"op":"replace","path":"/notes/a~1b","value":10}]""", "/notes/a~1b", "10")]
    [InlineData("""[{"op":"replace","path":"/notes/m~0n","value":20}]""", "/notes/m~0n", "20")]
    [InlineData("""[{"op":"add","path":"/assignee","value":null}]""", "/assignee", "null")]
    [InlineData("""[{"op":"replace","path":"","value":{"id":"WO-1003"}}]""", "/id", "\"WO-1003\"")]
    public void Each_operation_applies(string patch, string pointer, string expected)
    {
        var result = JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json(patch));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, At(result.Document, pointer));
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"/missing","value":1}]""")]          // target must exist
    [InlineData("""[{"op":"remove","path":"/checklist/2"}]""")]                 // index out of range
    [InlineData("""[{"op":"add","path":"/checklist/01","value":1}]""")]         // leading zeros aren't indexes
    [InlineData("""[{"op":"test","path":"/id","value":"WO-9999"}]""")]          // a failed test fails the patch
    [InlineData("""[{"op":"move","from":"/checklist","path":"/checklist/0"}]""")] // into its own child
    [InlineData("""[{"op":"add","path":"/a/b","value":1}]""")]                  // parent doesn't exist
    [InlineData("""[{"op":"remove","path":""}]""")]                              // the root can't be removed
    public void A_patch_that_does_not_apply_leaves_the_document_alone(string patch)
    {
        var document = JsonNode.Parse(WorkOrder);

        // The first operation would apply; the second fails; neither sticks.
        var result = JsonPatch.Apply(document, Json($$"""[{"op":"add","path":"/touched","value":true},{{patch[1..^1]}}]"""));

        Assert.Equal(JsonPatchResultKind.NotApplied, result.Kind);
        Assert.Same(document, result.Document);
        Assert.Equal(WorkOrder, document!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"op":"add","path":"/x"}""")]             // add without a value
    [InlineData("""{"op":"replace","value":1}""")]           // no path
    [InlineData("""{"op":"add","path":"x","value":1}""")]    // a pointer must start with '/'
    [InlineData("""{"op":"add","path":"/a~2b","value":1}""")] // ~2 isn't an escape
    [InlineData("""{"op":"copy","path":"/x"}""")]             // copy without from
    [InlineData("""{"path":"/x","value":1}""")]               // no op
    [InlineData("""{"op":7,"path":"/x"}""")]                 // op isn't a string
    [InlineData("""{"op":"remove","path":3}""")]               // path isn't a string
    [InlineData("""["add","/x",1]""")]                         // an operation must be an object
    public void A_malformed_operation_fails_the_patch_as_malformed(string operation)
    {
        var result = JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json($"[{operation}]"));

        Assert.False(result.Succeeded);
        Assert.Equal(JsonPatchResultKind.Malformed, result.Kind);
    }

    [Fact]
    public void Malformed_is_judged_for_the_whole_patch_before_anything_applies()
    {
        // The first operation alone wouldn't apply, but the patch is malformed, and that's what counts.
        var result = JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json("""[{"op":"remove","path":"/missing"},{"op":"add","path":"/x"}]"""));

        Assert.Equal(JsonPatchResultKind.Malformed, result.Kind);
        Assert.Equal(JsonPatchResultKind.NotApplied, JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json("""[{"op":"remove","path":"/missing"}]""")).Kind);
        Assert.Equal(JsonPatchResultKind.Applied, JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json("""[]""")).Kind);
    }

    [Fact]
    public void An_operation_rfc_6902_does_not_define_is_skipped_with_a_warning()
    {
        // Unrecognized, not malformed: its other members aren't judged (this one has no path at all).
        var result = JsonPatch.Apply(JsonNode.Parse(WorkOrder), Json("""[{"op":"increment","by":1},{"op":"add","path":"/ok","value":true}]"""));

        Assert.True(result.Succeeded);
        Assert.Equal("true", At(result.Document, "/ok"));
        Assert.Contains("increment", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void The_input_document_is_never_modified()
    {
        var document = JsonNode.Parse(WorkOrder);

        var result = JsonPatch.Apply(document, Json("""[{"op":"remove","path":"/checklist"}]"""));

        Assert.NotSame(document, result.Document);
        Assert.Equal(WorkOrder, document!.ToJsonString());
    }

    [Fact]
    public void A_patch_that_is_not_an_array_is_malformed()
    {
        Assert.Equal(JsonPatchResultKind.Malformed, JsonPatch.Apply(new JsonObject(), Json("""{"op":"add","path":"/x","value":1}""")).Kind);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string At(JsonNode? document, string pointer)
    {
        var node = document;
        foreach (var segment in pointer.Split('/').Skip(1).Select(s => s.Replace("~1", "/").Replace("~0", "~")))
        {
            node = node is JsonArray array ? array[int.Parse(segment)] : node![segment];
        }

        return node?.ToJsonString() ?? "null";
    }
}
