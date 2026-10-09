using System.Net;

namespace Ch08.Field.Core.Connection;

/// <summary>
/// How hard a client tries to open a run on a bad network, and which failures are worth another try.
/// Retries happen only before a run's first event (see <see cref="ResilientAgentTransport"/>).
/// </summary>
public sealed record ConnectionPolicy
{
    /// <summary>Three attempts, starting half a second apart and never more than five seconds apart.</summary>
    public static ConnectionPolicy Default { get; } = new();

    /// <summary>How many times to send a run's input before giving up, counting the first.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The longest wait before the second attempt; each later wait may be twice as long.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest any one wait may be. A server that asks for a longer wait isn't retried.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The clock the waits use. Tests replace it.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>A number from 0 to 1 that scales each wait (full jitter), so a crowd of phones doesn't retry in step.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>
    /// The wait before attempt <paramref name="attempt"/> (2 or more): exponential, capped and jittered. After a
    /// <see cref="RetryAfterException"/> it is the server's wait plus that backoff, still capped, or
    /// <see langword="null"/> when the server asked for longer than <see cref="MaxDelay"/>: then the client
    /// gives up rather than come back early.
    /// </summary>
    /// <param name="attempt">The attempt about to be made.</param>
    /// <param name="failure">What the previous attempt failed with, if known.</param>
    public TimeSpan? DelayBefore(int attempt, Exception? failure = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 2);
        var ceiling = BaseDelay * Math.Pow(2, Math.Min(attempt - 2, 16));
        var backoff = (ceiling < MaxDelay ? ceiling : MaxDelay) * Math.Clamp(Jitter(), 0, 1);

        return failure switch
        {
            RetryAfterException { RetryAfter: var asked } when asked > MaxDelay => null,
            RetryAfterException { RetryAfter: var asked } => asked + backoff < MaxDelay ? asked + backoff : MaxDelay,
            _ => backoff,
        };
    }

    /// <summary>
    /// Whether a failure that happened <b>before a run's first event</b> is worth another try with the
    /// same input: only a failure that shows the run never started. Anything that may have reached the
    /// agent isn't retried, because this chapter's agent keeps no record of run IDs and would run a copy.
    /// Never call this for a failure after the first event.
    /// </summary>
    /// <param name="exception">What the transport threw.</param>
    public static bool IsSafeToRetry(Exception exception) => exception switch
    {
        // Refused before the run started: overloaded or rate limited. Not 502 or 504: the gateway that sent
        // them may have forwarded the request to a run that is executing.
        HttpRequestException { StatusCode: { } status } => status
            is HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests,

        // The connection was never made (DNS, a refused or timed-out connect, see FieldHttp), so the request never left.
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError } => true,

        // Everything else may have reached the agent: a reset or an early end after the request was sent,
        // a timeout waiting for the first event, a cancellation.
        _ => false,
    };
}
