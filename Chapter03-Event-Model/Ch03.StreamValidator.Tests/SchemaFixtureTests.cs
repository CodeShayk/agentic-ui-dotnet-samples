using System.Text.Json;

namespace Ch03.StreamValidator.Tests;

/// <summary>
/// Checks enforcement against the schema fixtures (spec/1.0/fixtures): single values the 1.0 JSON
/// Schema accepts or rejects. Each invalid fixture's .expect.json names the failing keyword and the
/// location inside the value.
/// </summary>
public sealed class SchemaFixtureTests
{
    public static TheoryData<string> Valid() => Fixtures("valid");

    public static TheoryData<string> Invalid() => Fixtures("invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Schema_valid_fixtures_raise_nothing(string fixture)
    {
        var (evt, _) = Load(fixture);

        var diagnostics = AGUIStreamValidator.CheckEvent(evt);

        Assert.True(diagnostics.Count == 0, $"{fixture}: {string.Join(Environment.NewLine, diagnostics)}");
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Schema_invalid_fixtures_are_flagged_where_the_schema_fails(string fixture)
    {
        var (evt, location) = Load(fixture);

        var diagnostics = AGUIStreamValidator.CheckEvent(evt);

        Assert.True(diagnostics.Any(d => (d.Path ?? string.Empty).StartsWith(location, StringComparison.Ordinal)),
            $"{fixture}: expected a finding at '{location}', got:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
    }

    /// <summary>
    /// The processing model's asymmetry: a value the protocol describes but the schema rejects is
    /// fatal (an error), while an unknown property or union member is stripped (a warning).
    /// </summary>
    [Theory]
    [MemberData(nameof(Invalid))]
    public void Severity_follows_the_processing_model(string fixture)
    {
        var (evt, location) = Load(fixture);
        var keyword = Expectation(fixture).GetProperty("keyword").GetString();
        var at = AGUIStreamValidator.CheckEvent(evt)
            .Where(d => (d.Path ?? string.Empty).StartsWith(location, StringComparison.Ordinal))
            .ToList();

        var unknownMaterial = keyword is "unevaluatedProperties" or "enum" || (keyword == "const" && location.EndsWith("/op", StringComparison.Ordinal));
        if (unknownMaterial)
        {
            // Two closed-outcome cases are contradictions the spec names, so they stay errors.
            Assert.Contains(at, d => d.Severity == DiagnosticSeverity.Warning || d.RuleId is "RUN009" or "SUB010");
        }
        else if (keyword is "type" or "required" or "minimum" or "maximum" or "minItems" or "pattern" or "not" or "const")
        {
            Assert.Contains(at, d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    private static TheoryData<string> Fixtures(string kind)
    {
        var root = Wire.Directory("fixtures");
        return
        [
            .. System.IO.Directory.GetDirectories(root)
                .Select(definition => Path.Combine(definition, kind))
                .Where(System.IO.Directory.Exists)
                .SelectMany(folder => System.IO.Directory.GetFiles(folder, "*.json"))
                .Where(file => !file.EndsWith(".expect.json", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Loads a fixture as an event. Message and interrupt fixtures are values rather than events, so
    /// they are wrapped in the event that carries them, and the expected location is shifted to match.
    /// </summary>
    private static (JsonElement Event, string Location) Load(string fixture)
    {
        var raw = File.ReadAllText(Path.Combine(Wire.Directory("fixtures"), fixture));
        var location = fixture.Contains("/invalid/", StringComparison.Ordinal)
            ? Expectation(fixture).GetProperty("instanceLocation").GetString()!
            : string.Empty;

        var (json, prefix) = fixture.Split('/')[0] switch
        {
            "UserMessage" or "ToolMessage" => ($$"""{"type":"MESSAGES_SNAPSHOT","messages":[{{raw}}]}""", "/messages/0"),
            "Interrupt" => ($$$"""{"type":"RUN_FINISHED","threadId":"t1","runId":"r1","outcome":{"type":"interrupt","interrupts":[{{{raw}}}]}}""",
                "/outcome/interrupts/0"),
            _ => (raw, string.Empty),
        };

        return (JsonDocument.Parse(json).RootElement.Clone(), prefix + location);
    }

    private static JsonElement Expectation(string fixture) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Wire.Directory("fixtures"), fixture[..^".json".Length] + ".expect.json"))).RootElement;
}
