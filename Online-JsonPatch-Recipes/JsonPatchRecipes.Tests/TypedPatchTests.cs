using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JsonPatchRecipes;
using Beacon.Client.Core;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 4: typed patches with Microsoft.AspNetCore.JsonPatch.SystemTextJson, applied by the client's own applier.</summary>
public sealed class TypedPatchTests
{
    [Fact]
    public void A_typed_patch_has_camelCase_paths_and_applies_on_the_client()
    {
        var patch = TypedPatches.ToElement(TypedPatches.SetDone(2, "C3", done: true));
        Capture.Write("typed", patch);

        Assert.Equal(2, patch.GetArrayLength());
        Sheets.AssertOp(patch[0], "test", "/workOrder/checklist/2/id", "\"C3\"");
        Sheets.AssertOp(patch[1], "replace", "/workOrder/checklist/2/done", "true");

        var result = JsonPatch.Apply(Sheets.Parse(), patch);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("true", Sheets.At(result.Document, "/workOrder/checklist/2/done"));
    }

    [Fact]
    public void Options_without_a_naming_policy_build_paths_the_client_cannot_resolve()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var patch = new JsonPatchDocument<Sheet>([], options);
        patch.Replace(s => s.WorkOrder.Status, "InProgress");
        var element = JsonSerializer.SerializeToElement(patch, options);
        Capture.Write("typed-pascal", element);

        Assert.Equal("/WorkOrder/Status", element[0].GetProperty("path").GetString());
        Assert.False(JsonPatch.Apply(Sheets.Parse(), element).Succeeded); // the sheet has workOrder, not WorkOrder
    }

    [Fact]
    public void Serializer_options_name_the_members_inside_values()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var patch = new JsonPatchDocument<Sheet>([], web);
        patch.Add(s => s.WorkOrder.Checklist, new ChecklistEntry("B1", "Replace run capacitor (CAP-45-5)", false));

        // The path came from the document's (camelCase) options; the value from the serializer's (default) options.
        var mixed = JsonSerializer.SerializeToElement(patch, new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

        Assert.Equal("/workOrder/checklist/-", mixed[0].GetProperty("path").GetString());
        Assert.True(mixed[0].GetProperty("value").TryGetProperty("Id", out _));
        Assert.False(mixed[0].GetProperty("value").TryGetProperty("id", out _));
    }
}
