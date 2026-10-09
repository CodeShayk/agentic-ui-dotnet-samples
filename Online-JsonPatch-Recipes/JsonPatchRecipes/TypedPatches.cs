using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace JsonPatchRecipes;

/// <summary>The sheet as a server that keeps typed state sees it.</summary>
public sealed record Sheet
{
    public int SchemaVersion { get; init; } = 2;

    public required WorkOrder WorkOrder { get; init; }
}

public sealed record WorkOrder
{
    public required string Id { get; init; }

    public required string Status { get; init; }

    public required List<ChecklistEntry> Checklist { get; init; }
}

public sealed record ChecklistEntry(string Id, string Text, bool Done);

/// <summary>
/// Server-side recipe: build a patch with <see cref="JsonPatchDocument{TModel}"/> from
/// <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c>, using expressions instead of strings.
/// </summary>
public static class TypedPatches
{
    // Reflection-based on purpose: the package's converter isn't accessible to the System.Text.Json source
    // generator (SYSLIB1220), so a JsonPatchDocument can't join a source-generated context.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(RecipesJsonContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    /// <summary>Ticks or clears the item at <paramref name="index"/>, pinned to its ID.</summary>
    public static JsonPatchDocument<Sheet> SetDone(int index, string itemId, bool done)
    {
        // The document's options name the path segments: with the camelCase Options above,
        // s => s.WorkOrder.Checklist[index].Done becomes /workOrder/checklist/2/done.
        var patch = new JsonPatchDocument<Sheet>([], Options);
        patch.Test(s => s.WorkOrder.Checklist[index].Id, itemId);
        patch.Replace(s => s.WorkOrder.Checklist[index].Done, done);
        return patch;
    }

    /// <summary>
    /// The patch as the value of a <c>STATE_DELTA</c>'s <c>delta</c>. Serialize with the same options the
    /// document was created with: those named the paths, these name the members inside each value.
    /// </summary>
    public static JsonElement ToElement(JsonPatchDocument<Sheet> patch) =>
        JsonSerializer.SerializeToElement(patch, Options);
}
