using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Ch03.EventTour;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ch03.StreamValidator.Tests;

/// <summary>
/// Posts each of the event tour's prompts to the running endpoint, reads the Server-Sent Events back
/// and validates them with the request in hand.
/// </summary>
public sealed class EventTourTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory =
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("Tour:EventDelayMilliseconds", "0")
            .UseSetting("Logging:LogLevel:Default", "Warning"));

    public static TheoryData<string> CleanScenarios() => [.. Tour.Scenarios.Where(s => s.Name != "broken").Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(CleanScenarios))]
    public async Task Every_scenario_streams_a_valid_quiet_run(string name)
    {
        var (input, events) = await RunAsync(Prompt(name));

        var report = AGUIStreamValidator.Validate(events, StreamValidatorOptions.FromRunAgentInput(input));

        Assert.True(report.Diagnostics.Count == 0, $"{name}:{Environment.NewLine}{string.Join(Environment.NewLine, report.Diagnostics)}");
    }

    [Fact]
    public async Task The_broken_scenario_breaks_the_rules_it_says_it_breaks()
    {
        var (input, events) = await RunAsync(Prompt("broken"));

        var report = AGUIStreamValidator.Validate(events, StreamValidatorOptions.FromRunAgentInput(input));

        Assert.Equal(["ENV004", "ENV005", "RUN003", "TOOL003", "TXT002", "TXT003", "VER001"], Wire.RuleIds(report));
        Assert.Equal(["ENV004", "ENV005", "RUN003", "TOOL003", "TXT002", "TXT003"],
            report.Errors.Select(d => d.RuleId).Distinct().Order(StringComparer.Ordinal));

        // The banned null is on RUN_STARTED's parentRunId, which no 1.0 compatibility shim repairs,
        // not on one of the legacy nulls (such as RUN_FINISHED's outcome) that consumers still repair.
        var nullForAbsent = Assert.Single(report.Diagnostics, d => d.RuleId == "ENV005");
        Assert.Equal(0, nullForAbsent.EventIndex);
        Assert.Equal("/parentRunId", nullForAbsent.Path);
        Assert.True(events[0].TryGetProperty("parentRunId", out var parentRunId));
        Assert.Equal(JsonValueKind.Null, parentRunId.ValueKind);
        Assert.False(events[6].TryGetProperty("outcome", out _), "The broken run's RUN_FINISHED omits its outcome.");
    }

    [Fact]
    public async Task The_tour_puts_all_31_event_types_on_the_wire()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in Tour.Scenarios.Where(s => s.Name != "broken"))
        {
            var (_, events) = await RunAsync(scenario.Prompt);
            seen.UnionWith(events.Select(e => e.GetProperty("type").GetString()!));
        }

        var all = typeof(AGUIEventTypes).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        Assert.Equal(31, all.Count);
        Assert.Empty(all.Except(seen));
    }

    [Fact]
    public async Task Events_are_framed_one_JSON_object_per_SSE_event_with_omitted_optionals()
    {
        var (_, events) = await RunAsync(Prompt("text"));

        var started = events[0];
        Assert.Equal("RUN_STARTED", started.GetProperty("type").GetString());
        Assert.Equal("t-1", started.GetProperty("threadId").GetString());
        Assert.Equal("1.0", started.GetProperty("protocolVersion").GetString());
        Assert.False(started.TryGetProperty("parentRunId", out _), "Absent means absent: an unset optional field is omitted, not null.");

        var finished = events[^1];
        Assert.Equal("RUN_FINISHED", finished.GetProperty("type").GetString());
        Assert.False(finished.TryGetProperty("outcome", out _), "An absent outcome means success.");
    }

    [Fact]
    public void Each_prompt_selects_its_own_scenario()
    {
        foreach (var scenario in Tour.Scenarios)
        {
            var input = new RunAgentInput
            {
                ThreadId = "t-1",
                RunId = "r-1",
                Messages = [new AGUIUserMessage { Id = "m-1", Content = new AGUIContent(scenario.Prompt) }],
            };

            Assert.Equal(scenario.Name, Tour.Choose(input).Name);
        }
    }

    private static string Prompt(string scenario) => Tour.Scenarios.Single(s => s.Name == scenario).Prompt;

    private async Task<(JsonElement Input, IReadOnlyList<JsonElement> Events)> RunAsync(string prompt)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = $$"""
            {"threadId":"t-1","runId":"r-1","messages":[{"id":"m-1","role":"user","content":{{JsonSerializer.Serialize(prompt)}}}]}
            """;

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var events = new List<JsonElement>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            events.Add(JsonDocument.Parse(item.Data).RootElement.Clone());
        }

        return (JsonDocument.Parse(body).RootElement.Clone(), events);
    }
}
