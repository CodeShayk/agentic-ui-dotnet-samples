using System.Net;
using AGUI.Abstractions;
using Ch08.Field.Core.Connection;
using Microsoft.Extensions.Time.Testing;

namespace Ch08.Tests;

/// <summary>The retry rule: before the first event, retry only what never started, with the same input; after it, never.</summary>
public sealed class ResilientTransportTests
{
    private static readonly ConnectionPolicy NoWait = ConnectionPolicy.Default with { BaseDelay = TimeSpan.Zero };

    private static RunAgentInput Input() => new() { ThreadId = "thread-1", RunId = "run-1", Messages = [] };

    [Fact]
    public async Task A_failed_connect_is_retried_with_the_same_input()
    {
        var inner = new ScriptedTransport(
            Attempt.Fails(new HttpRequestException(HttpRequestError.ConnectionError, "No route to host.")),
            Attempt.Answers("WO-1001 is Scheduled."));
        var transport = new ResilientAgentTransport(inner, NoWait);
        List<ConnectionEvent> stages = [];
        transport.ConnectionChanged += (_, e) => stages.Add(e);

        var input = Input();
        var events = await Collect(transport.SendAsync(input, TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Inputs.Count);
        Assert.All(inner.Inputs, sent => Assert.Same(input, sent)); // same object, so the same run ID
        Assert.Equal(["RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED"], events.Select(e => e.Type));
        Assert.Equal(
            [(1, ConnectionStage.Retrying), (2, ConnectionStage.Started), (2, ConnectionStage.Finished)],
            stages.Select(s => (s.Attempt, s.Stage)));
    }

    [Fact]
    public async Task A_drop_after_the_first_event_is_a_truncated_run_and_is_not_retried()
    {
        var reset = new IOException("Connection reset by peer.");
        var inner = new ScriptedTransport(Attempt.Answers("Ordering CAP-45-5", breakAfter: reset, keep: 3), Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);
        List<BaseEvent> received = [];

        var error = await Assert.ThrowsAsync<TruncatedRunException>(async () =>
        {
            await foreach (var evt in transport.SendAsync(Input(), TestContext.Current.CancellationToken))
            {
                received.Add(evt);
            }
        });

        Assert.Single(inner.Inputs);
        Assert.Equal(3, received.Count);
        Assert.Equal(("run-1", 3, "TEXT_MESSAGE_CONTENT"), (error.RunId, error.EventsReceived, error.LastEventType));
        Assert.IsAssignableFrom<IOException>(error); // so AgentConversation calls it StreamEnded, a truncated run
        Assert.Same(reset, error.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    [InlineData(HttpStatusCode.Conflict)] // Chapter 24's run ledger: this run ID already reached the server
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)] // a gateway may have forwarded the request to a run that is executing
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_status_that_does_not_prove_a_safe_refusal_is_not_retried(HttpStatusCode status)
    {
        var inner = new ScriptedTransport(Attempt.Fails(Attempt.Status(status)), Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken)));
        Assert.Single(inner.Inputs);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refusal_before_the_run_started_is_retried(HttpStatusCode status)
    {
        var inner = new ScriptedTransport(Attempt.Fails(Attempt.Status(status)), Attempt.Answers("Ready."));
        var transport = new ResilientAgentTransport(inner, NoWait);

        var events = await Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Inputs.Count);
        Assert.Equal("RUN_FINISHED", events[^1].Type);
    }

    [Fact]
    public async Task A_reset_after_the_request_was_sent_is_not_retried_because_the_run_may_have_started()
    {
        var inner = new ScriptedTransport(
            Attempt.Fails(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.")),
            Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken)));
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task A_server_s_retry_after_is_waited_out_before_the_next_attempt()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        DateTimeOffset? secondAttempt = null;
        var inner = new ScriptedTransport(
            Attempt.Fails(new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(2))),
            (input, ct) =>
            {
                secondAttempt = time.GetUtcNow();
                return Attempt.Answers("Ready.")(input, ct);
            });
        var transport = new ResilientAgentTransport(inner, NoWait with { TimeProvider = time });
        TimeSpan? retryDelay = null;
        transport.ConnectionChanged += (_, e) => retryDelay ??= e.RetryDelay;

        // Move the fake clock in small steps until the run ends; the second attempt can't start early.
        var run = Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken));
        for (var step = 0; !run.IsCompleted && step < 200; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal("RUN_FINISHED", (await run)[^1].Type);
        Assert.Equal(TimeSpan.FromSeconds(2), retryDelay);
        Assert.True(secondAttempt - start >= TimeSpan.FromSeconds(2), $"second attempt after {secondAttempt - start}");
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_policy_allows_ends_the_attempts()
    {
        var inner = new ScriptedTransport(
            Attempt.Fails(new RetryAfterException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2))),
            Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);

        var error = await Assert.ThrowsAsync<RetryAfterException>(() => Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken)));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task A_timeout_waiting_for_the_first_event_is_not_retried_because_the_run_may_be_executing()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
        var inner = new ScriptedTransport(Attempt.Fails(timeout), Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);

        await Assert.ThrowsAsync<TaskCanceledException>(() => Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken)));
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task It_gives_up_after_the_policy_s_attempts()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "No route to host.");
        var inner = new ScriptedTransport(Attempt.Fails(refused), Attempt.Fails(refused), Attempt.Fails(refused), Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, NoWait);
        List<ConnectionStage> stages = [];
        transport.ConnectionChanged += (_, e) => stages.Add(e.Stage);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken)));

        Assert.Same(refused, error);
        Assert.Equal(3, inner.Inputs.Count);
        Assert.Equal([ConnectionStage.Retrying, ConnectionStage.Retrying, ConnectionStage.GaveUp], stages);
    }

    [Fact]
    public async Task Stop_during_the_backoff_cancels_without_another_attempt()
    {
        var inner = new ScriptedTransport(Attempt.Fails(new HttpRequestException(HttpRequestError.ConnectionError, "No route to host.")), Attempt.Answers("never sent"));
        var transport = new ResilientAgentTransport(inner, ConnectionPolicy.Default with { BaseDelay = TimeSpan.FromSeconds(30), Jitter = () => 1 });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        transport.ConnectionChanged += (_, e) =>
        {
            if (e.Stage == ConnectionStage.Retrying)
            {
                stop.Cancel(); // the user pressed Stop while the phone waited for a signal
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(transport.SendAsync(Input(), stop.Token)));
        Assert.Single(inner.Inputs);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_terminal_event_is_reported_truncated()
    {
        var inner = new ScriptedTransport(Attempt.Answers("WO-1001 is", keep: 3));
        var transport = new ResilientAgentTransport(inner, NoWait);
        List<ConnectionStage> stages = [];
        transport.ConnectionChanged += (_, e) => stages.Add(e.Stage);

        var events = await Collect(transport.SendAsync(Input(), TestContext.Current.CancellationToken));

        Assert.Equal(3, events.Count);
        Assert.Equal([ConnectionStage.Started, ConnectionStage.Truncated], stages);
    }

    [Fact]
    public void Backoff_grows_exponentially_with_full_jitter_up_to_the_cap()
    {
        var policy = ConnectionPolicy.Default with { Jitter = () => 1 };

        Assert.Equal(
            [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)],
            Enumerable.Range(2, 5).Select(attempt => policy.DelayBefore(attempt)!.Value));
        Assert.Equal(TimeSpan.Zero, (policy with { Jitter = () => 0 }).DelayBefore(4));

        // Retry-After comes first, with the backoff on top so a crowd still spreads out, never past the cap.
        Assert.Equal(TimeSpan.FromSeconds(1.5), policy.DelayBefore(2, new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(1))));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayBefore(3, new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(4.5))));
        Assert.Null(policy.DelayBefore(2, new RetryAfterException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30))));
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
}
