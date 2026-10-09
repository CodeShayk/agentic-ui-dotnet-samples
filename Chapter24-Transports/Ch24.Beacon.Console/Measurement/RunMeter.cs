using System.Diagnostics;
using System.Net;
using AGUI.Abstractions;

namespace Ch24.Beacon.Console.Measurement;

/// <summary>
/// What one run looked like from the client: when each milestone arrived, measured from the moment
/// the user asked, and how many bytes crossed the wire. Retries count against the same clock.
/// </summary>
public sealed class RunMeter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>How many HTTP requests the run took (more than one when a retry happened).</summary>
    public int Attempts { get; internal set; }

    public HttpStatusCode? Status { get; internal set; }

    public Version? HttpVersion { get; internal set; }

    public string? ContentType { get; internal set; }

    /// <summary>The body of a refused request (a problem details document, usually).</summary>
    public string? ErrorBody { get; internal set; }

    /// <summary>When the response headers arrived.</summary>
    public TimeSpan? Headers { get; internal set; }

    /// <summary>When <c>RUN_STARTED</c> arrived.</summary>
    public TimeSpan? RunStarted { get; internal set; }

    /// <summary>When the first <c>TEXT_MESSAGE_CONTENT</c> arrived: the first words the user can read.</summary>
    public TimeSpan? FirstText { get; internal set; }

    /// <summary>When the terminal event arrived, or the run failed.</summary>
    public TimeSpan? Finished { get; internal set; }

    /// <summary>Response body bytes, as the formatter read them (HTTP framing not included).</summary>
    public long Bytes { get; internal set; }

    public int Events { get; internal set; }

    /// <summary><c>success</c>, <c>interrupt</c>, <c>error:CODE</c>, <c>truncated</c> or <c>failed:...</c>.</summary>
    public string Outcome { get; internal set; } = "truncated";

    public TimeSpan Now => _clock.Elapsed;

    internal void Record(BaseEvent evt, long bytesSoFar)
    {
        Events++;
        Bytes = bytesSoFar;
        switch (evt)
        {
            case RunStartedEvent:
                RunStarted ??= Now;
                break;
            case TextMessageContentEvent:
                FirstText ??= Now;
                break;
            case RunFinishedEvent finished:
                Finished = Now;
                Outcome = finished.Outcome is RunFinishedInterruptOutcome ? "interrupt" : "success";
                break;
            case RunErrorEvent error:
                Finished = Now;
                Outcome = $"error:{error.Code}";
                break;
        }
    }

    public void Fail(Exception exception)
    {
        Finished ??= Now;
        Outcome = exception switch
        {
            HttpRequestException { StatusCode: { } status } => $"failed:{(int)status}",
            TaskCanceledException { InnerException: TimeoutException } or TimeoutException => "failed:timeout",
            _ => Events > 0 ? "truncated" : $"failed:{exception.GetType().Name}",
        };
    }
}
