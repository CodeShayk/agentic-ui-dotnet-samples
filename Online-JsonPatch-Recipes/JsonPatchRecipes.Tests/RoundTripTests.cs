using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using JsonPatchRecipes;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace JsonPatchRecipes.Tests;

/// <summary>
/// Recipe 7: testing patches. Every patch the server-side recipes build is applied with the client's
/// applier; diffs of generated documents must reproduce their target; deltas must survive the SDK.
/// </summary>
public sealed class RoundTripTests
{
    // Member names that exercise the pointer escapes and the tokens that look special.
    private static readonly string[] Names = ["id", "done", "notes", "C/HERM", "a~b", "~1", "-", "0", ""];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 200)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Applying_a_diff_reproduces_the_document_it_was_computed_from(int seed)
    {
        var random = new Random(seed);
        var before = RandomObject(random, depth: 0);
        var after = Mutate(before, random, edits: 1 + random.Next(6));

        foreach (var guard in (bool[])[false, true])
        {
            var patch = PatchOps.ToElement(JsonDiff.Create(before, after, guard));
            Assert.False(PatchShape.Check(patch).IsMalformed, $"seed {seed}");

            var result = JsonPatch.Apply(before, patch);
            Assert.True(result.Succeeded, $"seed {seed}, guard {guard}: {result.Error}");
            Assert.True(JsonNode.DeepEquals(after, result.Document), $"seed {seed}, guard {guard}");
        }
    }

    [Fact]
    public void Every_recipe_builds_well_formed_patches()
    {
        var sheet = Sheets.Parse();
        JsonArray[] patches =
        [
            ChecklistPatches.Add(new JsonObject { ["id"] = "B1", ["text"] = "Replace run capacitor (CAP-45-5)", ["done"] = false }),
            ChecklistPatches.SetDone(sheet, "C3", done: true)!,
            ChecklistPatches.MoveAfter(sheet, "C1", afterItemId: "C3")!,
            ChecklistPatches.Remove(sheet, "C2"),
            [PatchOps.Replace(JsonPointer.Create("workOrder", "readings", "C/HERM"), 44.6)],
        ];

        foreach (var patch in patches)
        {
            Assert.Null(PatchShape.Check(PatchOps.ToElement(patch)).Problem);
            Assert.True(JsonPatch.Apply(sheet, PatchOps.ToElement(patch)).Succeeded);
        }

        Assert.False(PatchShape.Check(TypedPatches.ToElement(TypedPatches.SetDone(2, "C3", done: true))).IsMalformed);
    }

    [Fact]
    public void A_delta_survives_the_SDK_serializer_and_the_client_reducer()
    {
        var patch = ChecklistPatches.SetDone(Sheets.Parse(), "C3", done: true)!;
        patch.Add(PatchOps.Add("/workOrder/eta", null));

        BaseEvent sent = new StateDeltaEvent { Delta = PatchOps.ToElement(patch) };
        var wire = JsonSerializer.Serialize(sent, AGUIJsonSerializerContext.Default.BaseEvent);
        Capture.Write("delta-event", wire);

        using (var frame = JsonDocument.Parse(wire))
        {
            Assert.Equal("STATE_DELTA", frame.RootElement.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, frame.RootElement.GetProperty("delta")[2].GetProperty("value").ValueKind);
        }

        var received = JsonSerializer.Deserialize(wire, AGUIJsonSerializerContext.Default.BaseEvent)!;
        var view = Reduce(ConversationView.Empty with { ThreadId = "t-e" }, new StateSnapshotEvent { Snapshot = Sheets.Json(Sheets.WorkOrder1001) });
        view = Reduce(view, received);

        Assert.False(view.State.NeedsResync);
        Assert.Equal("true", Sheets.At(view.State.Document, "/workOrder/checklist/2/done"));
        Assert.Equal("null", Sheets.At(view.State.Document, "/workOrder/eta"));
    }

    private static ConversationView Reduce(ConversationView view, BaseEvent evt) =>
        ConversationReducer.Reduce(view, new UpdateReceived(new ChatResponseUpdate { RawRepresentation = evt }));

    private static JsonNode? RandomValue(Random random, int depth) => random.Next(depth > 2 ? 4 : 6) switch
    {
        0 => null,
        1 => JsonValue.Create(random.Next(2) == 0),
        2 => JsonValue.Create(random.Next(-5, 50)),
        3 => JsonValue.Create(Names[random.Next(Names.Length)]),
        4 => RandomObject(random, depth + 1),
        _ => RandomArray(random, depth + 1),
    };

    private static JsonObject RandomObject(Random random, int depth)
    {
        var result = new JsonObject();
        for (var i = random.Next(1, 5); i > 0; i--)
        {
            result[Names[random.Next(Names.Length)]] = RandomValue(random, depth);
        }

        return result;
    }

    private static JsonArray RandomArray(Random random, int depth)
    {
        var result = new JsonArray();
        for (var i = random.Next(5); i > 0; i--)
        {
            result.Add(RandomValue(random, depth));
        }

        return result;
    }

    // Edits a copy: sets, adds and removes members, inserts and removes elements, anywhere in the tree.
    private static JsonNode Mutate(JsonNode before, Random random, int edits)
    {
        var after = before.DeepClone();
        for (var edit = 0; edit < edits; edit++)
        {
            var containers = Containers(after).ToList();
            switch (containers[random.Next(containers.Count)])
            {
                case JsonObject obj when obj.Count > 0 && random.Next(3) == 0:
                    obj.Remove(obj.ElementAt(random.Next(obj.Count)).Key);
                    break;
                case JsonObject obj:
                    obj[Names[random.Next(Names.Length)]] = RandomValue(random, depth: 2);
                    break;
                case JsonArray array when array.Count > 0 && random.Next(3) == 0:
                    array.RemoveAt(random.Next(array.Count));
                    break;
                case JsonArray array:
                    array.Insert(random.Next(array.Count + 1), RandomValue(random, depth: 2));
                    break;
            }
        }

        return after;
    }

    private static IEnumerable<JsonNode> Containers(JsonNode? node)
    {
        IEnumerable<JsonNode?> children = node switch
        {
            JsonObject obj => obj.Select(member => member.Value),
            JsonArray array => array,
            _ => [],
        };

        if (node is JsonObject or JsonArray)
        {
            yield return node;
        }

        foreach (var child in children.ToList())
        {
            foreach (var container in Containers(child))
            {
                yield return container;
            }
        }
    }
}
