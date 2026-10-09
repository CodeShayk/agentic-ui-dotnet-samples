using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonPatchRecipes;

/// <summary>
/// Builds RFC 6902 operations as <see cref="JsonObject"/>s, and turns a patch into the
/// <see cref="JsonElement"/> that <c>StateDeltaEvent.Delta</c> carries.
/// </summary>
public static class PatchOps
{
    public static JsonObject Add(string path, JsonNode? value) => WithValue("add", path, value);

    public static JsonObject Replace(string path, JsonNode? value) => WithValue("replace", path, value);

    public static JsonObject Test(string path, JsonNode? value) => WithValue("test", path, value);

    public static JsonObject Remove(string path) => new() { ["op"] = "remove", ["path"] = path };

    public static JsonObject Move(string from, string path) => new() { ["op"] = "move", ["from"] = from, ["path"] = path };

    public static JsonObject Copy(string from, string path) => new() { ["op"] = "copy", ["from"] = from, ["path"] = path };

    /// <summary>The patch as the value of a <c>STATE_DELTA</c>'s <c>delta</c>.</summary>
    public static JsonElement ToElement(JsonArray patch) =>
        JsonSerializer.SerializeToElement(patch, RecipesJsonContext.Default.JsonArray);

    // add, replace and test require "value", and JSON null is a legitimate value, so the member is always
    // written: { "op": "replace", "path": "/x", "value": null } sets x to null. The node is copied because
    // a JsonNode can belong to only one parent.
    private static JsonObject WithValue(string op, string path, JsonNode? value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value?.DeepClone() };
}
