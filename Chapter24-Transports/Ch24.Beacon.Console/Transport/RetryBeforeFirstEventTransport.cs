using System.Net;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch24.Beacon.Console.Transport;

/// <summary>
/// Retries a run only while nothing has arrived, with the same input (same run ID). Before the first event
/// the user has seen nothing, but the first copy may still have reached the server and started the run: a
/// 502, a 504 or a reset can't say. Only a server-side run ledger makes such a retry safe, by answering a
/// repeated run ID with 409 instead of running it twice; Beacon's agent keeps one. Against a server without
/// one, retry only what proves the run never started: a failed connect, a 503 or a 429 (Chapter 8). From
/// the first event on, the run belongs to the server, and a failure is a truncated run to report, never one
/// to repeat.
/// </summary>
public sealed class RetryBeforeFirstEventTransport(
    IAGUITransport inner,
    int maxAttempts = 3,
    Func<int, TimeSpan>? backoff = null,
    Action<int, Exception>? onRetry = null) : IAGUITransport
{
    /// <summary>The longest <c>Retry-After</c> this client waits; a server that asks for more gets no retry.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    private readonly Func<int, TimeSpan> _backoff = backoff ?? FullJitter;

    public async IAsyncEnumerable<BaseEvent> SendAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var events = inner.SendAsync(input, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool any;
            try
            {
                any = await events.MoveNextAsync();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSafeToRetry(ex) && !cancellationToken.IsCancellationRequested
                                       && DelayBefore(attempt, ex) is { } delay)
            {
                onRetry?.Invoke(attempt, ex);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (!any)
            {
                yield break;
            }

            // The first event is in: no more retries, whatever happens next.
            yield return events.Current;
            while (await events.MoveNextAsync())
            {
                yield return events.Current;
            }

            yield break;
        }
    }

    /// <summary>Failures that happen before a run's first event and are worth another try.</summary>
    public static bool IsSafeToRetry(Exception exception) => exception switch
    {
        // Refused before the stream opened: overloaded or rate limited, or a gateway that gave up. A 502 or 504
        // may come from a run that started, so retrying them leans on the agent's run ledger.
        HttpRequestException { StatusCode: { } status } => status
            is HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout,

        // No response at all: connection refused or reset, DNS, a response that ended early.
        HttpRequestException => true,
        IOException => true,

        // A connect timeout (a TimeoutException with nothing inside, Chapter 6): the run never reached the server.
        TaskCanceledException { InnerException: TimeoutException { InnerException: null } } => true,

        // 400, 401, 403, 406 and 409: trying again won't change the answer.
        _ => false,
    };

    // At least what the server asked for, plus the jittered backoff, so refused clients don't return in step.
    // A longer Retry-After than this client will wait means no retry at all.
    private TimeSpan? DelayBefore(int attempt, Exception exception) => exception switch
    {
        RetryAfterException { RetryAfter: var asked } when asked > MaxRetryAfter => null,
        RetryAfterException { RetryAfter: var asked } => asked + _backoff(attempt),
        _ => _backoff(attempt),
    };

    // Exponential backoff with full jitter (0-500 ms, then 0-1 s, ...), so a crowd of rejected
    // clients doesn't come back in step.
    private static TimeSpan FullJitter(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250 << Math.Min(attempt, 6)));
}
