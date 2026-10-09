using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonPatchRecipes;

/// <summary>
/// Patches built from what the agent means to do to the sheet's checklist, rather than from a diff.
/// Each one is computed against the document the client is known to hold, and every operation that
/// addresses an item by index first tests that the index still holds that item.
/// </summary>
public static class ChecklistPatches
{
    public const string ChecklistPath = "/workOrder/checklist";

    /// <summary>Appends an item. <c>-</c> means "after the last element", whatever the client's array holds.</summary>
    public static JsonArray Add(JsonObject item) =>
        [PatchOps.Add(JsonPointer.Append(ChecklistPath, JsonPointer.End), item)];

    /// <summary>
    /// Ticks or clears an item. Returns an empty patch when the item is already in that state and
    /// <see langword="null"/> when the sheet has no such item.
    /// </summary>
    public static JsonArray? SetDone(JsonNode? sheet, string itemId, bool done)
    {
        var index = IndexOf(sheet, itemId);
        if (index < 0)
        {
            return null;
        }

        if ((Items(sheet)![index] as JsonObject)?["done"]?.GetValueKind() == (done ? JsonValueKind.True : JsonValueKind.False))
        {
            return [];
        }

        var item = JsonPointer.Append(ChecklistPath, index);
        return
        [
            PatchOps.Test(JsonPointer.Append(item, "id"), itemId),
            PatchOps.Replace(JsonPointer.Append(item, "done"), done),
        ];
    }

    /// <summary>
    /// Moves an item so that it follows <paramref name="afterItemId"/>, or to the top when that is
    /// <see langword="null"/>. Both identities are pinned, so the move fails rather than lands in the
    /// wrong place if someone else reordered the list first.
    /// </summary>
    public static JsonArray? MoveAfter(JsonNode? sheet, string itemId, string? afterItemId)
    {
        var from = IndexOf(sheet, itemId);
        var anchor = afterItemId is null ? -1 : IndexOf(sheet, afterItemId);
        if (from < 0 || (afterItemId is not null && anchor < 0) || itemId == afterItemId)
        {
            return null;
        }

        // RFC 6902 performs a move as a remove followed by an add, so "path" indexes the array *without*
        // the item. An anchor earlier in the list keeps its index when the item is removed; a later one drops by one.
        var to = anchor < 0 ? 0 : anchor < from ? anchor + 1 : anchor;
        if (to == from)
        {
            return [];
        }

        JsonArray patch = [PatchOps.Test(JsonPointer.Append(JsonPointer.Append(ChecklistPath, from), "id"), itemId)];
        if (anchor >= 0)
        {
            patch.Add(PatchOps.Test(JsonPointer.Append(JsonPointer.Append(ChecklistPath, anchor), "id"), afterItemId));
        }

        patch.Add(PatchOps.Move(JsonPointer.Append(ChecklistPath, from), JsonPointer.Append(ChecklistPath, to)));
        return patch;
    }

    /// <summary>Removes an item, or returns an empty patch when it's already gone.</summary>
    public static JsonArray Remove(JsonNode? sheet, string itemId)
    {
        var index = IndexOf(sheet, itemId);
        if (index < 0)
        {
            return [];
        }

        var item = JsonPointer.Append(ChecklistPath, index);
        return [PatchOps.Test(JsonPointer.Append(item, "id"), itemId), PatchOps.Remove(item)];
    }

    /// <summary>The item's position now. A position is not an identity: it changes whenever the list does.</summary>
    public static int IndexOf(JsonNode? sheet, string itemId)
    {
        var items = Items(sheet);
        for (var i = 0; i < (items?.Count ?? 0); i++)
        {
            if ((items![i] as JsonObject)?["id"] is JsonValue id && id.TryGetValue<string>(out var value) && value == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    private static JsonArray? Items(JsonNode? sheet) => ((sheet as JsonObject)?["workOrder"] as JsonObject)?["checklist"] as JsonArray;
}
