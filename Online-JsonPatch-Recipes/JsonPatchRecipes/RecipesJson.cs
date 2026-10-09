using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JsonPatchRecipes;

/// <summary>
/// Source-generated metadata for the recipes: camelCase like the rest of the wire, compact and
/// omitting nothing, because a patch's <c>"value": null</c> is data.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(Sheet))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal sealed partial class RecipesJsonContext : JsonSerializerContext;
