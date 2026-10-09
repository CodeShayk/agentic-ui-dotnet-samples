using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ch27.Tests;

/// <summary>
/// Builds the Desk's HTTP setup: the service defaults, plus the AG-UI policy when asked, with the
/// agent's address supplied the way the AppHost supplies it (services__beacon-agent__http__0).
/// </summary>
internal static class DeskHttp
{
    public static IHost Build(Uri agent, bool aguiPolicy, Func<HttpMessageHandler>? primaryHandler = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Services:beacon-agent:http:0"] = agent.GetLeftPart(UriPartial.Authority);
        builder.AddServiceDefaults();
        var agentClient = builder.Services.AddHttpClient("BeaconAgent");
        if (aguiPolicy)
        {
            agentClient.AddAGUIStreamResilience();
        }

        if (primaryHandler is not null)
        {
            agentClient.ConfigurePrimaryHttpMessageHandler(primaryHandler); // replaces the policy's SocketsHttpHandler
        }

        return builder.Build();
    }

    public static async Task<List<ChatResponseUpdate>> AskAsync(IHost desk, string question, CancellationToken cancellationToken)
    {
        // The Desk's own endpoint: service discovery resolves the name, so removing the standard
        // resilience handler must leave discovery in place.
        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent");
        using var chat = new AGUIChatClient(new AGUIChatClientOptions(http, "https+http://beacon-agent/agent"));

        List<ChatResponseUpdate> updates = [];
        await foreach (var update in chat.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, question) { MessageId = "m-1" }], new ChatOptions(), cancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    /// <summary>A primary handler whose connection attempts are counted and never succeed.</summary>
    public static Func<HttpMessageHandler> Unreachable(StrongBox<int> attempts, bool hang) => () => new SocketsHttpHandler
    {
        // The policy's ConnectTimeout is 10 seconds; the test's is short, the exception is the same.
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        ConnectCallback = async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref attempts.Value);
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // a host that never answers the SYN
            }

            throw new SocketException((int)SocketError.ConnectionRefused); // a host with nothing listening
        },
    };
}

public sealed class StreamResilienceTests
{
    // Longer than the standard handler's 10-second attempt timeout.
    private static readonly TimeSpan SlowFirstEvent = TimeSpan.FromSeconds(11);

    private const string Question = "What is the status of WO-1001?";

    [Fact]
    public async Task A_run_whose_first_event_takes_longer_than_the_attempt_timeout_completes_and_is_posted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await SlowAgent.StartAsync(SlowFirstEvent, ct);
        using var desk = DeskHttp.Build(agent.Address, aguiPolicy: true);

        var clock = Stopwatch.StartNew();
        var updates = await DeskHttp.AskAsync(desk, Question, ct);

        Assert.True(clock.Elapsed > SlowFirstEvent);
        Assert.IsType<RunStartedEvent>(updates.First(u => u.RawRepresentation is BaseEvent).RawRepresentation);
        Assert.Contains(updates, u => u.RawRepresentation is RunFinishedEvent);
        Assert.Contains(updates, u => u.Text.Length > 0);
        Assert.Equal(1, agent.RunsStarted);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)] // a draining replica, or Chapter 24's concurrency limit
    [InlineData(HttpStatusCode.TooManyRequests)]    // Chapter 23's per-user limit, when the wait is short
    public async Task A_run_refused_with_a_short_Retry_After_is_sent_again_and_completes(HttpStatusCode refusal)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await SlowAgent.StartAsync(TimeSpan.Zero, ct, refuseFirst: 1, refusal: refusal);
        using var desk = DeskHttp.Build(agent.Address, aguiPolicy: true);

        var updates = await DeskHttp.AskAsync(desk, Question, ct);

        Assert.Contains(updates, u => u.RawRepresentation is RunFinishedEvent);
        Assert.Equal((1, 1), (agent.Refusals, agent.RunsStarted)); // refused once, started once
    }

    [Fact]
    public async Task A_429_that_asks_for_a_long_wait_goes_back_to_the_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await SlowAgent.StartAsync(TimeSpan.Zero, ct, refuseFirst: 3, refusal: HttpStatusCode.TooManyRequests, retryAfter: "60");
        using var desk = DeskHttp.Build(agent.Address, aguiPolicy: true);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => DeskHttp.AskAsync(desk, Question, ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(1, agent.Refusals); // not retried: a minute is the dispatcher's call, not the policy's
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, false)] // a proxy's 503 may follow a run that started
    [InlineData(HttpStatusCode.BadGateway, false, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, false, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, false)]
    public void Only_a_refusal_that_says_retry_after_counts_as_never_started(HttpStatusCode status, bool retryAfter, bool expected)
    {
        using var response = new HttpResponseMessage(status);
        if (retryAfter)
        {
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(expected, AGUIStreamResilience.RunNeverStarted(Outcome.FromResult(response)));
    }

    [Fact]
    public void A_connection_that_was_never_made_counts_as_never_started()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made.");
        var connectTimeout = new TaskCanceledException("The operation was canceled.", new TimeoutException("A connection could not be established within the configured ConnectTimeout."));
        var cut = new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        var headerTimeout = new TaskCanceledException("timeout", new TimeoutException("HttpClient.Timeout", new TaskCanceledException()));

        Assert.True(AGUIStreamResilience.RunNeverStarted(Outcome.FromException<HttpResponseMessage>(refused)));
        Assert.True(AGUIStreamResilience.RunNeverStarted(Outcome.FromException<HttpResponseMessage>(connectTimeout)));
        Assert.False(AGUIStreamResilience.RunNeverStarted(Outcome.FromException<HttpResponseMessage>(cut)));
        Assert.False(AGUIStreamResilience.RunNeverStarted(Outcome.FromException<HttpResponseMessage>(headerTimeout)));
    }

    [Fact]
    public async Task A_connect_timeout_is_retried_and_reaches_the_caller_as_a_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = new StrongBox<int>();
        using var desk = DeskHttp.Build(new Uri("http://beacon-agent.invalid:5270"), aguiPolicy: true, DeskHttp.Unreachable(attempts, hang: true));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeskHttp.AskAsync(desk, Question, ct));

        // What SocketsHttpHandler throws when ConnectTimeout expires: never an HttpRequestException.
        Assert.IsType<TaskCanceledException>(thrown);
        Assert.Null(Assert.IsType<TimeoutException>(thrown.InnerException).InnerException);
        Assert.Equal(3, attempts.Value); // the first attempt and the policy's two retries
    }

    [Fact]
    public async Task The_breaker_opens_at_a_dispatch_desks_volume_and_then_fails_fast()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = new StrongBox<int>();
        using var desk = DeskHttp.Build(new Uri("http://beacon-agent.invalid:5270"), aguiPolicy: true, DeskHttp.Unreachable(attempts, hang: false));

        // Two questions: three refused connects, then the fourth opens the breaker (4 attempts, all failed).
        await Assert.ThrowsAsync<HttpRequestException>(() => DeskHttp.AskAsync(desk, Question, ct));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => DeskHttp.AskAsync(desk, Question, ct));
        Assert.Equal(4, attempts.Value);

        // The third question doesn't try to connect at all.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => DeskHttp.AskAsync(desk, Question, ct));
        Assert.Equal(4, attempts.Value);
    }

    [Fact]
    public async Task Refusals_with_Retry_After_never_open_the_breaker()
    {
        var ct = TestContext.Current.CancellationToken;

        // A replica that refuses six runs in a row while it drains. Counted as failures, they would open the
        // breaker during the second question; they aren't, so the third question gets through.
        await using var agent = await SlowAgent.StartAsync(TimeSpan.Zero, ct, refuseFirst: 6);
        using var desk = DeskHttp.Build(agent.Address, aguiPolicy: true);

        for (var question = 0; question < 2; question++)
        {
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => DeskHttp.AskAsync(desk, Question, ct));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode); // three attempts each
        }

        var updates = await DeskHttp.AskAsync(desk, Question, ct);
        Assert.Contains(updates, u => u.RawRepresentation is RunFinishedEvent);
        Assert.Equal((6, 1), (agent.Refusals, agent.RunsStarted));
    }
}

/// <summary>The control: the same slow run through the standard handler from the service defaults.</summary>
public sealed class StandardHandlerTests
{
    [Fact]
    public async Task The_standard_handler_cancels_a_slow_starting_run_posts_it_again_and_gives_up()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await SlowAgent.StartAsync(TimeSpan.FromSeconds(11), ct);
        using var desk = DeskHttp.Build(agent.Address, aguiPolicy: false);

        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeskHttp.AskAsync(desk, "What is the status of WO-1001?", ct));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Gave up after {clock.Elapsed.TotalSeconds:0.0} s with {failure.GetType().Name}; the agent started {agent.RunsStarted} runs.");
        Assert.True(failure is TimeoutRejectedException || failure.InnerException is TimeoutRejectedException, failure.ToString());
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(40)); // the 30-second total timeout
        Assert.True(agent.RunsStarted >= 2, $"The agent started {agent.RunsStarted} run(s).");
    }
}
