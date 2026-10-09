using System.Text.Json;

namespace Ch03.StreamValidator.Tests;

/// <summary>
/// Runs the validator over the specification's conformance streams (spec/1.0/conformance/streams).
/// Each fixture says whether a conforming consumer accepts the stream ("completed") or rejects it
/// ("failed"); the validator's errors must agree.
/// </summary>
public sealed class ConformanceStreamTests
{
    /// <summary>
    /// Fixtures that pin a reference implementation's behavior where it contradicts the
    /// specification. The validator follows the specification. See the fixture's description and
    /// the conformance README ("What these clients do that the specification says they should NOT").
    /// </summary>
    private static readonly Dictionary<string, string> AdmittedGaps = new()
    {
        // A role outside the closed set is an unrecognized member: stripped with a warning, not fatal.
        ["unknown-enum-value-role-fatal"] = "ENV007",
    };

    public static TheoryData<string> Streams() =>
        [.. System.IO.Directory.GetFiles(Wire.Directory("streams"), "*.json").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Streams))]
    public void Verdict_matches_the_specification(string name)
    {
        var (fixture, report) = Run(name);
        var consumerRejects = fixture.GetProperty("expect").GetProperty("outcome").GetString() == "failed";

        if (AdmittedGaps.TryGetValue(name, out var rule))
        {
            Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Errors));
            Assert.Contains(report.Warnings, d => d.RuleId == rule);
            return;
        }

        Assert.True(consumerRejects != report.IsValid,
            $"{name}: the fixture expects a consumer to {(consumerRejects ? "reject" : "accept")} the stream, " +
            $"but the validator reported:{Environment.NewLine}{string.Join(Environment.NewLine, report.Diagnostics)}");
    }

    [Theory]
    [InlineData("chunk-conflicting-role-fatal", "CHK002")]
    [InlineData("chunk-conflicting-tool-name-fatal", "CHK002")]
    [InlineData("content-without-start-fatal", "TXT002")]
    [InlineData("custom-closes-chunk-stream-fatal", "CHK001")]
    [InlineData("event-after-run-finished-fatal", "RUN003")]
    [InlineData("first-chunk-missing-id-fatal", "CHK001")]
    [InlineData("first-event-not-run-started-fatal", "RUN001")]
    [InlineData("first-tool-chunk-missing-name-fatal", "CHK001")]
    [InlineData("malformed-known-field-fatal", "ENV004")]
    [InlineData("nested-run-started-fatal", "RUN002")]
    [InlineData("open-message-at-run-finished-fatal", "TXT003")]
    [InlineData("reasoning-discipline-verified", "RSN002")]
    [InlineData("reasoning-message-wrong-role-fatal", "ENV004")]
    [InlineData("run-finished-with-active-subagent-fatal", "SUB004")]
    [InlineData("step-finished-without-start-fatal", "STEP002")]
    [InlineData("subagent-attribution-mismatch-fatal", "SUB005")]
    [InlineData("subagent-finished-unstarted-fatal", "SUB003")]
    [InlineData("subagent-started-missing-name-fatal", "ENV003")]
    [InlineData("unknown-outcome-array-fatal", "ENV004")]
    public void Fatal_fixtures_report_the_rule_they_exercise(string name, string ruleId)
    {
        var (_, report) = Run(name);

        Assert.Contains(report.Errors, d => d.RuleId == ruleId);
    }

    /// <summary>The warnings each accepted fixture is about. Anything else a clean fixture raises is a validator bug.</summary>
    private static readonly Dictionary<string, string[]> ExpectedWarnings = new()
    {
        ["activity-delta-missing-target-tolerated"] = ["ACT001"],
        ["encrypted-value-unknown-entity-tolerated"] = ["RSN007"],
        ["era-0-0-45-thinking-translated"] = ["ENV002"], // THINKING_* is unknown to 1.0; the online Migration Guide covers the rename.
        ["producer-declares-newer-version-warns"] = ["VER002"],
        ["producer-declares-uninterpretable-version-warns"] = ["VER002"],
        ["state-delta-unknown-op-dropped"] = ["ENV007"],
        ["unknown-enum-value-role-fatal"] = ["ENV007"],
        ["unknown-event-dropped"] = ["ENV002"],
        ["unknown-outcome-stripped"] = ["ENV007"],
        ["unknown-part-in-message-list"] = ["ENV007"],
        ["unknown-property-stripped"] = ["ENV006"],
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void Accepted_fixtures_raise_only_the_warnings_they_are_about(string name)
    {
        var (fixture, report) = Run(name);
        if (!report.IsValid)
        {
            return; // Covered by the verdict test.
        }

        var allowed = new HashSet<string>(ExpectedWarnings.GetValueOrDefault(name, []));
        var stream = fixture.GetProperty("stream").EnumerateArray().ToList();

        // Many fixtures predate the rule that a 1.0 producer declares its version, or use an unprefixed
        // CUSTOM name; both are producer-side advice that a consumer never acts on.
        if (stream.Any(e => Type(e) == "RUN_STARTED" && !e.TryGetProperty("protocolVersion", out _)))
        {
            allowed.Add("VER001");
        }

        if (stream.Any(e => Type(e) == "CUSTOM" && e.GetProperty("name").GetString()!.IndexOfAny(['.', ':', '/']) < 0))
        {
            allowed.Add("PASS001");
        }

        var unexplained = report.Warnings.Where(d => !allowed.Contains(d.RuleId)).ToList();
        Assert.True(unexplained.Count == 0, $"{name}: {string.Join(Environment.NewLine, unexplained)}");

        foreach (var expected in ExpectedWarnings.GetValueOrDefault(name, []))
        {
            Assert.Contains(report.Warnings, d => d.RuleId == expected);
        }
    }

    [Fact]
    public void Stripped_content_part_is_reported_at_the_path_the_fixture_names()
    {
        var (fixture, report) = Run("unknown-part-in-message-list");
        var expectedPath = fixture.GetProperty("expect").GetProperty("warnings")[0].GetString();

        Assert.Contains(report.Warnings, d => d.RuleId == "ENV007" && d.Path == expectedPath);
    }

    [Fact]
    public void Every_stream_in_the_manifest_is_here()
    {
        var manifest = File.ReadAllLines(Path.Combine(Wire.Directory("streams"), "MANIFEST.txt"))
            .Where(line => line.EndsWith(".json", StringComparison.Ordinal))
            .Select(line => Path.GetFileNameWithoutExtension(line))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(manifest, Streams().Select(row => row.Data).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(68, manifest.Count);
    }

    private static (JsonElement Fixture, ValidationReport Report) Run(string name)
    {
        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Wire.Directory("streams"), name + ".json"))).RootElement;
        return (fixture, AGUIStreamValidator.Validate(fixture.GetProperty("stream").EnumerateArray()));
    }

    private static string? Type(JsonElement evt) => evt.TryGetProperty("type", out var type) ? type.GetString() : null;
}
