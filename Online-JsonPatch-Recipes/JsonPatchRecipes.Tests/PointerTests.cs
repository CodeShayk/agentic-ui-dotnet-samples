using JsonPatchRecipes;
using Beacon.Client.Core;

namespace JsonPatchRecipes.Tests;

/// <summary>Recipe 1: pointers built from segments, with RFC 6901's two escapes in the right order.</summary>
public sealed class PointerTests
{
    [Theory]
    [InlineData("C/HERM", "C~1HERM")]
    [InlineData("~1", "~01")]      // a literal "~1" stays two characters
    [InlineData("a~/b", "a~0~1b")]
    [InlineData("-", "-")]          // "-" is special only as the last segment of an array path
    [InlineData("", "")]            // the empty member name is legal JSON
    public void Escape_and_unescape_round_trip(string member, string token)
    {
        Assert.Equal(token, JsonPointer.Escape(member));
        Assert.Equal(member, JsonPointer.Unescape(token));
    }

    [Fact]
    public void A_reading_keyed_by_terminals_is_addressed_through_its_escaped_name()
    {
        // Priya replaced the capacitor and measured the new one across C and HERM.
        var path = JsonPointer.Create("workOrder", "readings", "C/HERM");
        var patch = new System.Text.Json.Nodes.JsonArray(PatchOps.Replace(path, 44.6));
        Capture.Write("pointer", patch);

        Assert.Equal("/workOrder/readings/C~1HERM", path);
        var sheet = Sheets.Applied(Sheets.Parse(), patch);
        Assert.Equal("44.6", Sheets.At(sheet, path));
        Assert.Equal("4.9", Sheets.At(sheet, "/workOrder/readings/C~1FAN"));
    }

    [Fact]
    public void An_unescaped_slash_names_a_location_that_does_not_exist()
    {
        // Without escaping, "C/HERM" is two segments: a member "C" holding a member "HERM".
        var patch = Sheets.Json("""[{"op":"replace","path":"/workOrder/readings/C/HERM","value":44.6}]""");
        var document = Sheets.Parse();

        var result = JsonPatch.Apply(document, patch);

        Assert.False(result.Succeeded);
        Assert.Same(document, result.Document);
    }

    [Theory]
    [InlineData("", true)]                 // the whole document
    [InlineData("/", true)]                // the member named ""
    [InlineData("/workOrder/readings/C~1HERM", true)]
    [InlineData("/workOrder/checklist/-", true)]
    [InlineData("workOrder", false)]       // no leading slash
    [InlineData("/a~2b", false)]           // ~ followed by something other than 0 or 1
    [InlineData("/a~", false)]
    [InlineData("#/workOrder", false)]     // a URI fragment is not a JSON Pointer
    [InlineData("\n", false)]              // .NET's $ would match before the line break; \z doesn't
    public void IsValid_follows_the_schema_pattern(string pointer, bool valid) =>
        Assert.Equal(valid, JsonPointer.IsValid(pointer));
}
