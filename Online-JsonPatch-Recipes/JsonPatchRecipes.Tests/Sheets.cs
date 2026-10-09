using System.Text.Json;
using System.Text.Json.Nodes;
using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>The document every recipe works on, and helpers to read and patch it.</summary>
internal static class Sheets
{
    /// <summary>
    /// Chapter 12's sheet for WO-1001, plus the readings Priya took from the dual run capacitor:
    /// microfarads, keyed by the terminals she measured across.
    /// </summary>
    public const string WorkOrder1001 = """
        {"schemaVersion":2,"workOrder":{"id":"WO-1001","status":"Scheduled","checklist":[
          {"id":"C1","text":"Check thermostat settings and wiring","done":false},
          {"id":"C2","text":"Measure refrigerant pressures","done":false},
          {"id":"C3","text":"Inspect run capacitor","done":false},
          {"id":"C4","text":"Test compressor amperage","done":false}],
          "readings":{"C/HERM":38.0,"C/FAN":4.9},
          "notes":["Customer reports the unit stops after a few minutes."]}}
        """;

    public static JsonNode Parse() => JsonNode.Parse(WorkOrder1001)!;

    public static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Applies a patch the way Beacon's clients do, and insists that it applied.</summary>
    public static JsonNode? Applied(JsonNode? document, JsonArray patch)
    {
        var result = JsonPatch.Apply(document, PatchOps.ToElement(patch));
        Assert.True(result.Succeeded, result.Error);
        return result.Document;
    }

    /// <summary>The checklist's item IDs, in order.</summary>
    public static string[] Ids(JsonNode? sheet) =>
        [.. (sheet?["workOrder"]?["checklist"] as JsonArray ?? []).Select(item => item!["id"]!.GetValue<string>())];

    /// <summary>The JSON at a pointer, or "(missing)".</summary>
    public static string At(JsonNode? document, string pointer)
    {
        var node = document;
        foreach (var token in pointer.Split('/').Skip(1).Select(JsonPointer.Unescape))
        {
            switch (node)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var child):
                    node = child;
                    break;
                case JsonArray array when int.TryParse(token, out var index) && index < array.Count:
                    node = array[index];
                    break;
                default:
                    return "(missing)";
            }
        }

        return node?.ToJsonString() ?? "null";
    }

    /// <summary>Asserts one operation member by member, never by comparing whole strings.</summary>
    public static void AssertOp(JsonElement operation, string op, string path, string? valueJson = null, string? from = null)
    {
        Assert.Equal(op, operation.GetProperty("op").GetString());
        Assert.Equal(path, operation.GetProperty("path").GetString());

        if (valueJson is null)
        {
            Assert.False(operation.TryGetProperty("value", out _));
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(Json(valueJson), operation.GetProperty("value")));
        }

        if (from is null)
        {
            Assert.False(operation.TryGetProperty("from", out _));
        }
        else
        {
            Assert.Equal(from, operation.GetProperty("from").GetString());
        }
    }
}

/// <summary>Writes a recipe's output to JSON_PATCH_CAPTURE_DIR, when it's set, for the outputs the recipes print.</summary>
internal static class Capture
{
    public static void Write(string name, JsonArray patch) => Write(name, patch.ToJsonString());

    public static void Write(string name, JsonElement json) => Write(name, json.GetRawText());

    public static void Write(string name, string json)
    {
        var directory = Environment.GetEnvironmentVariable("JSON_PATCH_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json + Environment.NewLine);
    }
}
