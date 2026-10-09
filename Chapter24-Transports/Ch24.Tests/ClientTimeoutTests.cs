using Ch24.Beacon.Console.Measurement;
using Ch24.Beacon.Console.Transport;

namespace Ch24.Tests;

/// <summary>
/// The console follows Chapter 6's timeouts: no HttpClient.Timeout, which would measure the model's first token,
/// a connect timeout that fails fast and counts as never started, and a bound on each run.
/// </summary>
public sealed class ClientTimeoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(WireFormat.Sse)]
    [InlineData(WireFormat.Protobuf)]
    public void The_client_has_no_header_timeout_and_a_ten_second_connect_timeout(WireFormat wire)
    {
        using var http = BeaconHttp.Create(wire);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        using var handler = BeaconHttp.CreateHandler(wire, http2: true);
        HttpMessageHandler inner = handler;
        while (inner is DelegatingHandler delegating)
        {
            inner = delegating.InnerHandler!;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(inner).ConnectTimeout);
    }

    [Fact]
    public void A_connect_timeout_is_retried_because_the_run_never_reached_the_server()
    {
        // The two shapes Chapter 6 tells apart: a connect timeout's TimeoutException has nothing inside.
        var connectTimeout = new TaskCanceledException("canceled", new TimeoutException("A connection could not be established within the configured ConnectTimeout."));
        var headerTimeout = new TaskCanceledException("canceled", new TimeoutException("HttpClient.Timeout", new TaskCanceledException()));

        Assert.True(RetryBeforeFirstEventTransport.IsSafeToRetry(connectTimeout));
        Assert.False(RetryBeforeFirstEventTransport.IsSafeToRetry(headerTimeout));
    }

    [Fact]
    public async Task A_run_that_outlives_its_bound_is_reported_as_timed_out()
    {
        // The model is silent for two seconds; the run's bound is 300 milliseconds.
        await using var host = new AgentHost(("Beacon:Simulation:FirstChunkDelay", "00:00:02"));
        using var http = host.CreateClient();

        var meter = await RunProbe.RunAsync(http, "/agent", "What is the status of WO-1001?", cancellationToken: Ct, runTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal("failed:timeout", meter.Outcome);
    }
}
