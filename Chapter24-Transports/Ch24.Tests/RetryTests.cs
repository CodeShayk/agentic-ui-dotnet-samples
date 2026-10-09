using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Ch24.Beacon.Console.Transport;
using Microsoft.Extensions.AI;

namespace Ch24.Tests;

public sealed class RetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_503_before_the_first_event_is_retried_with_the_same_run_id()
    {
        var inner = new ScriptedTransport(
            _ => throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable),
            input => Events(input));
        var retrying = new RetryBeforeFirstEventTransport(inner, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        var input = AgentHost.Run();
        var events = await Collect(retrying.SendAsync(input, Ct));

        Assert.Equal(2, inner.Inputs.Count);
        Assert.All(inner.Inputs, sent => Assert.Equal(input.RunId, sent.RunId));
        Assert.IsType<RunFinishedEvent>(events[^1]);
    }

    [Fact]
    public async Task A_retry_waits_at_least_as_long_as_Retry_After_asks()
    {
        var inner = new ScriptedTransport(
            _ => throw new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMilliseconds(500)),
            input => Events(input));
        var retrying = new RetryBeforeFirstEventTransport(inner, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        var clock = Stopwatch.StartNew();
        var events = await Collect(retrying.SendAsync(AgentHost.Run(), Ct));

        // Task.Delay can return a timer tick early (about 15 ms on Windows), so allow a few ticks.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(450), $"Retried after {clock.Elapsed.TotalMilliseconds:0} ms.");
        Assert.Equal(2, inner.Inputs.Count);
        Assert.IsType<RunFinishedEvent>(events[^1]);
    }

    [Fact]
    public async Task A_Retry_After_longer_than_the_client_waits_is_not_retried()
    {
        var inner = new ScriptedTransport(_ => throw new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(1)), input => Events(input));
        var retrying = new RetryBeforeFirstEventTransport(inner, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        await Assert.ThrowsAsync<RetryAfterException>(() => Collect(retrying.SendAsync(AgentHost.Run(), Ct)));
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task Retry_After_survives_AGUIChatClient_when_the_handler_reads_it()
    {
        // One permit, held by a slow run; the next run is refused with 503 and Retry-After: 1.
        await using var host = new AgentHost(("Beacon:Streaming:MaxConcurrentRuns", "1"), ("Beacon:Simulation:FirstChunkDelay", "00:00:03"));
        using var holder = host.CreateClient();
        using var held = await AgentHost.PostAsync(holder, AgentHost.Run(), AgentHost.SseType, Ct);

        using var http = host.CreateDefaultClient(new RetryAfterHandler());
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, "/agent"));
        var refused = await Assert.ThrowsAsync<RetryAfterException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }], cancellationToken: Ct));

        Assert.Equal((HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(1)), (refused.StatusCode, refused.RetryAfter));
        Assert.True(RetryBeforeFirstEventTransport.IsSafeToRetry(refused));
    }

    [Fact]
    public async Task A_failure_after_the_first_event_is_never_retried()
    {
        var inner = new ScriptedTransport(input => EventsThenBreak(input));
        var retrying = new RetryBeforeFirstEventTransport(inner, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        List<BaseEvent> received = [];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var evt in retrying.SendAsync(AgentHost.Run(), Ct))
            {
                received.Add(evt);
            }
        });

        Assert.Single(inner.Inputs);
        Assert.IsType<RunStartedEvent>(Assert.Single(received));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Refusals_that_another_try_wont_change_are_not_retried(HttpStatusCode status)
    {
        var inner = new ScriptedTransport(_ => throw new HttpRequestException(status.ToString(), null, status), input => Events(input));
        var retrying = new RetryBeforeFirstEventTransport(inner, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(retrying.SendAsync(AgentHost.Run(), Ct)));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task A_retry_of_a_request_that_did_arrive_is_refused_by_the_ledger_not_run_twice()
    {
        await using var host = new AgentHost();
        using var http = host.CreateClient();
        var sdkTransport = new AGUIChatClientOptions(http, "/agent").Transport;

        // The first attempt reaches the server and starts the run, then the connection drops before
        // the client reads a single event. The retry carries the same run ID.
        var flaky = new ScriptedTransport(
            input => ReachServerThenDrop(http, input),
            input => sdkTransport.SendAsync(input, Ct));
        var retrying = new RetryBeforeFirstEventTransport(flaky, maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        using var client = new AGUIChatClient(new AGUIChatClientOptions { Transport = retrying });
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the status of WO-1001?") { MessageId = "user-1" }], cancellationToken: Ct));

        // 409: the server already has this run. A production client looks before it re-runs (Chapter 22).
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(2, flaky.Inputs.Count);
    }

    private static async IAsyncEnumerable<BaseEvent> ReachServerThenDrop(HttpClient http, RunAgentInput input)
    {
        using (var response = await http.PostAsJsonAsync("/agent", input, AGUIJsonSerializerContext.Default.RunAgentInput, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        throw new IOException("The connection was reset before the first event.");
#pragma warning disable CS0162 // Unreachable code: the iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<BaseEvent> Events(RunAgentInput input)
    {
        await Task.Yield();
        yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
        yield return new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
    }

    private static async IAsyncEnumerable<BaseEvent> EventsThenBreak(RunAgentInput input)
    {
        await Task.Yield();
        yield return new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId };
        throw new IOException("The response ended prematurely.");
    }

    private static async Task<List<BaseEvent>> Collect(IAsyncEnumerable<BaseEvent> events)
    {
        List<BaseEvent> list = [];
        await foreach (var evt in events)
        {
            list.Add(evt);
        }

        return list;
    }

    /// <summary>A transport whose attempts follow a script; the last behavior repeats.</summary>
    private sealed class ScriptedTransport(params Func<RunAgentInput, IAsyncEnumerable<BaseEvent>>[] attempts) : IAGUITransport
    {
        public List<RunAgentInput> Inputs { get; } = [];

        public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            await foreach (var evt in attempts[Math.Min(Inputs.Count, attempts.Length) - 1](input).WithCancellation(cancellationToken))
            {
                yield return evt;
            }
        }
    }
}
