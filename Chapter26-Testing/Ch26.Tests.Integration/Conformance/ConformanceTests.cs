using System.Net;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AI;
using Ch03.StreamValidator;
using Ch26.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ch26.Tests.Integration.Conformance;

/// <summary>
/// Section 26.4: Beacon's streams checked against the protocol rather than against Beacon. Chapter 3's
/// validator applies the 1.0 ordering and shape rules to every scenario, and the specification's own
/// RunAgentInput fixtures check that Beacon accepts what the schema accepts and refuses what it rejects.
/// </summary>
public sealed class ConformanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Conformance", "RunAgentInput");

    [Theory]
    [MemberData(nameof(Scenarios.All), MemberType = typeof(Scenarios))]
    public async Task Beacons_streams_pass_the_protocol_validator(string scenario)
    {
        foreach (var run in await Scenarios.RunAsync(scenario))
        {
            // The request tells the validator which thread and run to expect and which tools are the client's.
            var report = AGUIStreamValidator.Validate(run.Frames, StreamValidatorOptions.FromRunAgentInput(run.InputJson));

            Assert.True(report.Diagnostics.Count == 0, Describe(scenario, report));
        }
    }

    public static TheoryData<string> ValidInputs => [.. Directory.GetFiles(Path.Combine(Fixtures, "valid"), "*.json").Select(Path.GetFileName).OfType<string>()];

    [Theory]
    [MemberData(nameof(ValidInputs))]
    public async Task Every_input_the_schema_accepts_gets_a_conformant_stream(string fixture)
    {
        var input = await File.ReadAllTextAsync(Path.Combine(Fixtures, "valid", fixture), Ct);
        await using var agent = new AgentHost(new ScriptedChatClient(BeaconScripts.Default, new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }));

        using var response = await Wire.SendAsync(agent.CreateClient(), input, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var frames = await Wire.ReadFramesAsync(response, Ct);
        var report = AGUIStreamValidator.Validate(frames, StreamValidatorOptions.FromRunAgentInput(JsonDocument.Parse(input).RootElement));
        Assert.True(report.IsValid, Describe(fixture, report));
    }

    public static TheoryData<string> InvalidInputs =>
    [
        .. Directory.GetFiles(Path.Combine(Fixtures, "invalid"), "*.json")
            .Where(f => !f.EndsWith(".expect.json", StringComparison.Ordinal))
            .Select(Path.GetFileName).OfType<string>(),
    ];

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Every_input_the_schema_rejects_is_refused_before_a_run_starts(string fixture)
    {
        var input = await File.ReadAllTextAsync(Path.Combine(Fixtures, "invalid", fixture), Ct);
        await using var agent = new AgentHost(); // a model with no turns: any call would fail the test

        using var response = await Wire.SendAsync(agent.CreateClient(), input, Ct);

        if (fixture == "messages-missing.json")
        {
            // A documented exception, pinned on purpose: AGUI.Server's RunAgentInput reads a missing
            // "messages" as an empty list, so Beacon answers with an ordinary run. Being lenient here
            // harms no client; if Beacon ever decides to refuse it, this line changes deliberately.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(agent.Recorded!.Calls); // no model call: the run never started
    }

    [Fact]
    public async Task The_sdk_cannot_convert_the_specs_full_example_which_is_why_Beacon_filters_it()
    {
        // full.json carries activity and reasoning messages. ToChatRequestContext 1.0.0 throws on both
        // (Appendix A), so BeaconRunHandler removes them first. This test fails the day the SDK accepts
        // them, which is the day the filter can go.
        await using var agent = await AgentHost.ReplayingAsync(Recordings.Status);
        var json = agent.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var input = JsonSerializer.Deserialize<RunAgentInput>(
            await File.ReadAllTextAsync(Path.Combine(Fixtures, "valid", "full.json"), Ct), json)!;

        var error = Assert.Throws<InvalidOperationException>(() => input.ToChatRequestContext(json));
        Assert.Contains("Unknown chat role", error.Message);
    }

    private static string Describe(string name, ValidationReport report) =>
        $"{name}: {report}{Environment.NewLine}{string.Join(Environment.NewLine, report.Diagnostics)}";
}
