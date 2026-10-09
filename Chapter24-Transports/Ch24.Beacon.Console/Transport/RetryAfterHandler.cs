// Copied from Chapter 8 (Ch08.Field.Core/Connection); each chapter's code is a self-contained checkpoint.
using System.Net;
using System.Net.Http.Headers;

namespace Ch24.Beacon.Console.Transport;

/// <summary>
/// Keeps the server's <c>Retry-After</c> on a 429 or a 503. The SDK's HTTP transport turns a failure status
/// into an <see cref="HttpRequestException"/> and discards the response, headers and all, so this handler
/// reads the header first and throws a <see cref="RetryAfterException"/> that carries it.
/// </summary>
/// <param name="timeProvider">The clock for a <c>Retry-After</c> given as a date; <see langword="null"/> uses the system clock.</param>
public sealed class RetryAfterHandler(TimeProvider? timeProvider = null) : DelegatingHandler
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            && Wait(response.Headers.RetryAfter) is { } retryAfter)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new RetryAfterException(status, retryAfter);
        }

        return response;
    }

    // Retry-After is either a number of seconds or an HTTP date.
    private TimeSpan? Wait(RetryConditionHeaderValue? header) => header switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } when date - _time.GetUtcNow() is var wait => wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };
}

/// <summary>
/// The agent refused a request with 429 or 503 and said when to come back. It is an
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestException.StatusCode"/> set, so everything
/// that classifies failures by status treats it as the refusal it is.
/// </summary>
/// <param name="statusCode">429 or 503.</param>
/// <param name="retryAfter">How long the server asked the client to wait.</param>
public sealed class RetryAfterException(HttpStatusCode statusCode, TimeSpan retryAfter)
    : HttpRequestException(
        HttpRequestError.Unknown,
        $"Response status code does not indicate success: {(int)statusCode}. The agent asked for a retry after {retryAfter.TotalSeconds:0.#} s.",
        inner: null,
        statusCode)
{
    /// <summary>How long the server asked the client to wait before trying again.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}
