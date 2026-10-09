using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace StreamOptionsCookbook;

/// <summary>Recipe: <see cref="AGUIStreamOptions.MapResultAsStateDelta"/> makes a tool's result a patch to the state.</summary>
public static class StateDeltaRecipe
{
    public const string SetChecklistItemName = "set_checklist_item";

    public static AIFunction SetChecklistItem { get; } = AIFunctionFactory.Create(
        ([Description("The checklist item's ID, for example C3.")] string itemId,
         [Description("True to tick the item, false to clear it.")] bool done) => Patch(itemId, done),
        SetChecklistItemName,
        "Ticks or clears a checklist item on the shared sheet. Returns the change as a JSON Patch; [] means nothing changed.",
        CookbookJson.ToolOptions);

    public static AGUIStreamOptions Apply(AGUIStreamOptions options) => options
        .MapResultAsStateDelta(SetChecklistItemName);

    private static JsonElement Patch(string itemId, bool done)
    {
        // The patch must apply to the document the client holds, which arrived as this run's state.
        var state = CookbookJson.CurrentRunState();
        if (state.TryGetProperty("checklist", out var checklist) && checklist.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in checklist.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueEquals(itemId))
                {
                    if (item.TryGetProperty("done", out var current) && current.ValueKind == (done ? JsonValueKind.True : JsonValueKind.False))
                    {
                        break; // already so
                    }

                    // Pin the item's identity first: if the client's list has moved, the test fails and the
                    // whole patch is rejected instead of ticking the wrong line.
                    return CookbookJson.ToElement(new JsonArray(
                        new JsonObject { ["op"] = "test", ["path"] = $"/checklist/{index}/id", ["value"] = itemId },
                        new JsonObject { ["op"] = "replace", ["path"] = $"/checklist/{index}/done", ["value"] = done }));
                }

                index++;
            }
        }

        // Nothing to change, or nothing it could change: an empty patch, never an error object, because
        // whatever this returns goes out as STATE_DELTA.
        return CookbookJson.ToElement(new JsonArray());
    }
}
