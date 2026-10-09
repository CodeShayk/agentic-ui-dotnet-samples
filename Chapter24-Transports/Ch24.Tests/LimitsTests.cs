using System.Diagnostics;
using System.Net;
using AGUI.Abstractions;
using AGUI.Formatting;
using Ch24.Beacon.Agent.Streaming;

namespace Ch24.Tests;

public sealed class LimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_beyond_the_concurrency_limit_gets_503_before_any_event()
    {
        await using var host = new AgentHost(
            ("Beacon:Streaming:MaxConcurrentRuns", "1"),
            ("Beacon:Simulation:FirstChunkDelay", "00:00:02"));
        using var http = host.CreateClient();

        using var first = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);
        using var second = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), second.Headers.RetryAfter?.Delta);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_client_that_disconnects_stops_its_run()
    {
        // One run at a time, and a model that would stay silent for 30 seconds.
        await using var host = new AgentHost(
            ("Beacon:Streaming:MaxConcurrentRuns", "1"),
            ("Beacon:Simulation:FirstChunkDelay", "00:00:30"));
        using var http = host.CreateClient();

        using (var leaving = new CancellationTokenSource())
        {
            using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, leaving.Token);
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            await foreach (var evt in new SseEventStreamFormatter().ReadAsync(body, leaving.Token))
            {
                Assert.IsType<RunStartedEvent>(evt);
                break; // the user closes the app after the run starts
            }

            await leaving.CancelAsync();
        }

        // The run held the only permit. A new run gets in quickly only if the first one was cancelled
        // all the way down to the model's 30-second wait.
        var clock = Stopwatch.StartNew();
        HttpStatusCode status;
        do
        {
            using var next = await AgentHost.PostAsync(http, AgentHost.Run("Hello"), AgentHost.SseType, Ct);
            status = next.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(50, Ct);
            }
        }
        while (status != HttpStatusCode.OK && clock.Elapsed < TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_run_that_outlives_its_budget_ends_with_RUN_TIMEOUT_not_a_cut_stream()
    {
        await using var host = new AgentHost(
            ("Beacon:Streaming:RunTimeout", "00:00:00.300"),
            ("Beacon:Simulation:FirstChunkDelay", "00:00:05"));
        using var http = host.CreateClient();

        using var response = await AgentHost.PostAsync(http, AgentHost.Run(), AgentHost.SseType, Ct);
        var events = await AgentHost.ReadEventsAsync(response, Ct);

        Assert.Collection(
            events,
            e => Assert.IsType<RunStartedEvent>(e),
            e => Assert.Equal(WireShaping.RunTimeoutCode, Assert.IsType<RunErrorEvent>(e).Code));
    }

    [Fact]
    public async Task A_run_id_is_accepted_once()
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();
        var input = AgentHost.Run();

        using var first = await AgentHost.PostAsync(http, input, AgentHost.SseType, Ct);
        await first.Content.ReadAsByteArrayAsync(Ct);
        using var again = await AgentHost.PostAsync(http, input, AgentHost.SseType, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("application/problem+json", again.Content.Headers.ContentType?.MediaType);
    }
}
