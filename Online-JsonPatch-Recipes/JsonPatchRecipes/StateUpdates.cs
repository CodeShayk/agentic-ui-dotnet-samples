using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;

namespace JsonPatchRecipes;

/// <summary>
/// Server-side recipe: choose the event that brings a client from <c>before</c> to <c>after</c>.
/// A delta saves bytes; a snapshot can't fail. This picks the delta only when it's clearly cheaper and
/// the server knows the document the client holds.
/// </summary>
public static class StateUpdates
{
    /// <summary>
    /// The <c>STATE_SNAPSHOT</c> or <c>STATE_DELTA</c> to send, or <see langword="null"/> when nothing changed.
    /// </summary>
    /// <param name="before">The document the client holds, as far as the server knows.</param>
    /// <param name="after">The document the client should hold.</param>
    /// <param name="baselineKnown">
    /// False when the server can't vouch for <paramref name="before"/>: after an error, after a delta that may
    /// have failed on the client, or when the shape or schema version of the state changed.
    /// </param>
    public static BaseEvent? Next(JsonNode? before, JsonNode? after, bool baselineKnown)
    {
        if (baselineKnown && JsonNode.DeepEquals(before, after))
        {
            return null;
        }

        if (!baselineKnown || before is not (JsonObject or JsonArray) || before.GetValueKind() != after?.GetValueKind())
        {
            return Snapshot(after);
        }

        // A guarded patch refuses to apply to a document that has drifted, which turns divergence into a
        // skipped delta and a resync instead of a silently wrong document.
        var patch = JsonDiff.Create(before, after, guard: true);
        var patchBytes = Utf8Length(PatchOps.ToElement(patch));
        var snapshotBytes = Utf8Length(ToElement(after));

        return patchBytes * 2 > snapshotBytes
            ? Snapshot(after)
            : new StateDeltaEvent { Delta = PatchOps.ToElement(patch) };
    }

    private static StateSnapshotEvent Snapshot(JsonNode? after) => new() { Snapshot = ToElement(after) };

    private static JsonElement ToElement(JsonNode? node) =>
        JsonSerializer.SerializeToElement(node, RecipesJsonContext.Default.JsonNode);

    private static int Utf8Length(JsonElement element) => Encoding.UTF8.GetByteCount(element.GetRawText());
}
