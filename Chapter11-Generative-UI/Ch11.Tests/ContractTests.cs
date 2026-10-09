using System.Text.Json;
using Ch11.Beacon.Components;
using Ch11.Beacon.ComponentViews;

namespace Ch11.Tests;

/// <summary>The contract both ends share: prop schemas, the validator and partial-JSON completion.</summary>
public sealed class ContractTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_catalog_uses_only_the_schema_keywords_the_validator_checks()
    {
        foreach (var contract in BeaconComponents.All)
        {
            Assert.All(Keywords(contract.Schema), keyword => Assert.Contains(keyword, PropSchemaValidator.SupportedKeywords));
            Assert.StartsWith("show_", contract.ToolName);
        }

        static IEnumerable<string> Keywords(JsonElement schema)
        {
            foreach (var property in schema.EnumerateObject())
            {
                yield return property.Name;
                var children = property.Name switch
                {
                    "properties" => property.Value.EnumerateObject().Select(p => p.Value),
                    "items" => [property.Value],
                    _ => [],
                };

                foreach (var keyword in children.SelectMany(Keywords))
                {
                    yield return keyword;
                }
            }
        }
    }

    [Fact]
    public void Valid_props_pass_and_every_violation_names_its_location()
    {
        var schema = BeaconComponents.PartCard.Schema;
        Assert.Empty(PropSchemaValidator.Validate(Json("""{"sku":"CAP-45-5","name":"Dual run capacitor 45/5 uF","unitPrice":38.50,"inStock":24}"""), schema));

        var errors = PropSchemaValidator.Validate(
            Json("""{"sku":"cap 45","unitPrice":-1,"inStock":2.5,"workOrderId":"WO-1","discount":10}"""), schema);

        Assert.Equal(
        [
            "$.name: is required.",
            "$.sku: doesn't have the expected format (^[A-Z]{3}-[A-Z0-9]+(-[A-Z0-9]+)?$).",
            "$.unitPrice: must be at least 0.",
            "$.inStock: must be an integer.",
            "$.workOrderId: doesn't have the expected format (^WO-[0-9]{4}$).",
            "$: has a property the schema doesn't allow.",
        ], errors);
    }

    [Fact]
    public void Nested_props_are_checked_down_to_each_slot()
    {
        var errors = PropSchemaValidator.Validate(
            Json("""{"date":"2026-11-31","lanes":[{"technicianId":"TECH-1","slots":[{"start":"09:00","end":"25:00"}]}]}"""),
            BeaconComponents.ScheduleTimeline.Schema);

        Assert.Equal(["$.date: isn't a valid date.", "$.lanes[0].slots[0].end: doesn't have the expected format (^([01][0-9]|2[0-3]):[0-5][0-9]$)."], errors);
    }

    [Fact]
    public async Task Every_prefix_of_a_streamed_card_completes_to_valid_json_that_only_grows()
    {
        var deltas = Recordings.ArgumentDeltas(await Recordings.LoadAsync(Recordings.PartCard), BeaconComponents.PartCard.ToolName);
        var full = string.Concat(deltas);
        Assert.True(deltas.Count > 5, "the arguments streamed in pieces");

        var seen = 0;
        for (var length = 1; length <= full.Length; length++)
        {
            if (PartialJson.TryComplete(full[..length]) is not { } draft)
            {
                continue;
            }

            // A draft never loses a property it had, and every property it has came from the arguments.
            var names = draft.EnumerateObject().Select(p => p.Name).ToList();
            Assert.True(names.Count >= seen);
            seen = names.Count;
            Assert.All(draft.EnumerateObject(), p => Assert.Contains($"\"{p.Name}\"", full));
        }

        Assert.True(JsonElement.DeepEquals(Json(full), PartialJson.TryComplete(full)!.Value));
    }

    [Theory]
    [InlineData("""{"sku":"CAP-45-5","na""", """{"sku":"CAP-45-5"}""")]                  // a key still arriving is dropped
    [InlineData("""{"sku":"CAP-45-5","name":"Dual run cap""", """{"sku":"CAP-45-5","name":"Dual run cap"}""")] // a value is shown as far as it goes
    [InlineData("""{"sku":"CAP-45-5","unitPrice":38.5""", """{"sku":"CAP-45-5"}""")]     // a number that may still grow waits
    [InlineData("""{"lanes":[{"technicianId":"TECH-1","slots":[{"start":"09:00", """, """{"lanes":[{"technicianId":"TECH-1","slots":[{"start":"09:00"}]}]}""")]
    [InlineData("""{"note":"Fits \u00""", """{"note":"Fits "}""")]                        // an unfinished escape is cut
    public void A_prefix_completes_to_what_it_implies(string prefix, string expected) =>
        Assert.True(JsonElement.DeepEquals(Json(expected), PartialJson.TryComplete(prefix)!.Value));

    [Fact]
    public void Nothing_renderable_gives_no_draft() =>
        Assert.Null(PartialJson.TryComplete("""  """));
}
