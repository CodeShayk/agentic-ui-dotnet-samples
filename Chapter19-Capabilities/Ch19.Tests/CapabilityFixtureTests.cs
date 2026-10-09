using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Ch19.Beacon.Adaptive.Capabilities;

namespace Ch19.Tests;

/// <summary>
/// The capability reader against the specification's own fixtures (spec/1.0/fixtures). Whole documents
/// are read as they are; group and record fixtures are placed where they live in a declaration.
/// </summary>
public sealed class CapabilityFixtureTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // Where each fixture folder's value sits inside an AgentCapabilities document.
    private static readonly Dictionary<string, (string Prefix, Func<string, string> Wrap)> Placement = new()
    {
        ["AgentCapabilities"] = ("", json => json),
        ["ExecutionCapabilities"] = ("/execution", json => $$$"""{"execution":{{{json}}}}"""),
        ["MultiAgentCapabilities"] = ("/multiAgent", json => $$$"""{"multiAgent":{{{json}}}}"""),
        ["SubagentInfo"] = ("/multiAgent/subagents/0", json => $$$"""{"multiAgent":{"subagents":[{{{json}}}]}}"""),
    };

    public static TheoryData<string> Valid() => Fixtures("valid");

    public static TheoryData<string> Invalid() => Fixtures("invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_declaration_is_used_and_round_trips_through_the_SDK(string fixture)
    {
        var (document, _) = Load(fixture);

        var read = CapabilityReader.Read(document);

        Assert.True(read.IsUsable, string.Join("; ", read.Problems));
        Assert.Empty(read.Unrecognized);
        var written = JsonSerializer.Serialize(read.Capabilities, AGUIJsonSerializerContext.Default.AgentCapabilities);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(document), JsonNode.Parse(written)), written);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_schema_invalid_declaration_is_rejected_only_when_a_known_value_is_malformed(string fixture)
    {
        var (document, prefix) = Load(fixture);
        using var expect = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, fixture.Replace(".json", ".expect.json", StringComparison.Ordinal))));
        var keyword = expect.RootElement.GetProperty("keyword").GetString();
        var location = prefix + expect.RootElement.GetProperty("instanceLocation").GetString();

        var read = CapabilityReader.Read(document);

        if (keyword == "unevaluatedProperties")
        {
            // A member this version doesn't define: the schema rejects it, a consumer must not.
            Assert.True(read.IsUsable, string.Join("; ", read.Problems));
            Assert.Contains(read.Unrecognized, pointer => pointer.StartsWith(location + "/", StringComparison.Ordinal));
        }
        else
        {
            Assert.False(read.IsUsable);
            Assert.Null(read.Capabilities);
            Assert.Contains(read.Problems, problem => problem.StartsWith(location + ":", StringComparison.Ordinal) || problem.StartsWith(location + "/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_old_subAgents_key_is_noted_and_the_subagents_stay_unknown()
    {
        var read = CapabilityReader.Read(File.ReadAllBytes(Path.Combine(Root, "AgentCapabilities", "invalid", "old-subagents-key.json")));

        Assert.Equal(["/multiAgent/subAgents"], read.Unrecognized);
        Assert.True(read.Capabilities!.MultiAgent!.Supported);
        Assert.Null(read.Capabilities.MultiAgent.Subagents); // not "none": the agent said nothing under the 1.0 key
    }

    [Fact]
    public void An_empty_declaration_is_valid_and_says_nothing()
    {
        var read = CapabilityReader.Read("{}");

        Assert.True(read.IsUsable);
        Assert.Equal(AgentProfile.Undeclared, AgentProfile.From(read.Capabilities));
    }

    [Theory]
    [InlineData("[]", "(document): not a JSON object")]
    [InlineData("{\"reasoning\":null}", "/reasoning: expected an object, found null")]
    [InlineData("{\"tools\":{\"items\":[{\"name\":\"x\"}]}}", "/tools/items/0: 'description' is required")]
    [InlineData("{\"output\":{\"supportedMimeTypes\":[\"text/plain\",42]}}", "/output/supportedMimeTypes/1: expected a string, found the number 42")]
    public void Problems_point_at_the_malformed_value(string json, string problem)
    {
        var read = CapabilityReader.Read(json);

        Assert.False(read.IsUsable);
        Assert.Contains(problem, read.Problems);
    }

    private static (byte[] Document, string Prefix) Load(string fixture)
    {
        var folder = fixture.Split('/')[0];
        var (prefix, wrap) = Placement[folder];
        var json = File.ReadAllText(Path.Combine(Root, fixture));
        return (System.Text.Encoding.UTF8.GetBytes(wrap(json)), prefix);
    }

    private static TheoryData<string> Fixtures(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var folder in Placement.Keys)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, folder, kind), "*.json").Where(f => !f.EndsWith(".expect.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                data.Add($"{folder}/{kind}/{Path.GetFileName(file)}");
            }
        }

        return data;
    }
}
