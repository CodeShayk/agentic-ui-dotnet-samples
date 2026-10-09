using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// HTTP resilience for a client that posts AG-UI runs. The standard handler from
/// <c>AddServiceDefaults</c> suits short calls; an AG-UI run is a long POST whose response headers
/// arrive with the run's first event, so its timeouts and retries are replaced here.
/// </summary>
public static class AGUIStreamResilience
{
    /// <summary>The name of the resilience pipeline this policy registers.</summary>
    public const string PipelineName = "agui-stream";

    /// <summary>The longest <c>Retry-After</c> worth waiting for; a longer one goes back to the user.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    public static IHttpClientBuilder AddAGUIStreamResilience(this IHttpClientBuilder http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // The standard handler would cancel a run whose first event takes longer than 10 seconds,
        // post it again, and give up after 30. Remove it from this client only.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental in Microsoft.Extensions.Http.Resilience 10.10.
        http.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        http.AddResilienceHandler(PipelineName, pipeline => pipeline
            // Retry only when the run provably never started (see RunNeverStarted). Without a run ledger shared by
            // every replica, nothing else is safe to send again.
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(250),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = true, // wait as long as a 503 or 429 asks
                ShouldHandle = args => ValueTask.FromResult(RunNeverStarted(args.Outcome) && RetryAfterOf(args.Outcome) <= MaxRetryAfter),
            })
            // Fail fast while the agent can't be reached instead of holding each dispatcher on a connect. Polly's
            // defaults need 100 requests in 30 seconds before the breaker may open, which a dispatch desk never
            // sends, so the numbers are Beacon-sized; and only failures to connect count, never a refusal.
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                FailureRatio = 0.5,
                BreakDuration = TimeSpan.FromSeconds(10),
                ShouldHandle = args => ValueTask.FromResult(CouldNotConnect(args.Outcome.Exception)),
            }));

        // No timeout on the exchange as a whole (AddResilienceHandler already switches HttpClient.Timeout
        // off; this says so). The agent bounds each run with its own budget and ends it with RUN_ERROR,
        // and the UI's Stop button cancels it from this side.
        http.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // Bound what is safe to bound: reaching the agent at all.
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // Stop means stop. By default a response disposed mid-stream is drained (up to 1 MB or 2 seconds) so the
            // connection can be reused, and a run with that little left finishes on the agent as if nobody had left.
            MaxResponseDrainSize = 0,

            // Each streaming run holds a connection for its whole length. Recycling pooled connections picks up
            // DNS changes; where clients reach replicas directly (not through a per-request proxy such as
            // Container Apps' ingress), it also lets new replicas share the load after a scale-out.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        });

        return http;
    }

    /// <summary>
    /// True only for failures that happen before the agent could start the run: no connection could be made
    /// (refused, a name that didn't resolve or a connect timeout), or the agent refused the run with 503 or 429
    /// and <c>Retry-After</c> (it does that while draining or over a limit). A 502, a 504, a 503 without
    /// <c>Retry-After</c> or a broken stream may follow a run that started, so a retry could repeat its tools.
    /// </summary>
    public static bool RunNeverStarted(Outcome<HttpResponseMessage> outcome) =>
        CouldNotConnect(outcome.Exception)
        || outcome.Result is { StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests, Headers.RetryAfter: not null };

    /// <summary>
    /// The request never reached the agent. <c>SocketsHttpHandler.ConnectTimeout</c> expires as a cancellation
    /// around a <see cref="TimeoutException"/> with nothing inside it, the shape <c>Beacon.Client.Core</c>'s
    /// <c>RunFailure.RunNeverStarted</c> recognizes (Chapter 6). <c>HttpClient.Timeout</c> can't be the cause
    /// here: it is infinite on this client, and applied outside the handlers anyway.
    /// </summary>
    public static bool CouldNotConnect(Exception? exception) => exception is
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }
        or OperationCanceledException { InnerException: TimeoutException { InnerException: null } };

    // How long the response asked the client to wait; zero when it didn't ask (a failed connect).
    private static TimeSpan RetryAfterOf(Outcome<HttpResponseMessage> outcome) =>
        outcome.Result?.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => TimeSpan.Zero,
        };
}
