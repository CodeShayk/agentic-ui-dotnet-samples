// Copied from Chapter 25 (Ch25.Beacon.Agent).
using System.Diagnostics;
using OpenTelemetry;

namespace Ch27.Beacon.Agent.Telemetry;

/// <summary>
/// Keeps exception messages out of exported spans. A message can quote a tool's arguments, a URL or a
/// model's output, and it reaches a trace two ways: <c>agui.run</c>, <c>chat</c> and <c>execute_tool</c>
/// record it as their status description, and <c>Activity.AddException</c>, which .NET's HttpClient calls
/// for every failed request, writes it with the stack trace into an <c>exception</c> event. A failed span
/// keeps its error status, <c>error.type</c> and <c>exception.type</c>.
/// </summary>
public sealed class ErrorDetailRedactor : BaseProcessor<Activity>
{
    private const string Redacted = "(redacted)";

    // AddException keeps a message and stack trace that a listener has already supplied.
    private readonly ActivityListener _exceptions = new()
    {
        ShouldListenTo = _ => true,
        ExceptionRecorder = static (Activity _, Exception _, ref TagList tags) =>
        {
            tags.Add("exception.message", Redacted);
            tags.Add("exception.stacktrace", Redacted);
        },
    };

    public ErrorDetailRedactor() => ActivitySource.AddActivityListener(_exceptions);

    public override void OnEnd(Activity activity)
    {
        // Runs before the exporters, which were registered after this processor.
        if (activity.Status == ActivityStatusCode.Error && !string.IsNullOrEmpty(activity.StatusDescription))
        {
            activity.SetStatus(ActivityStatusCode.Error); // no description
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exceptions.Dispose();
        }

        base.Dispose(disposing);
    }
}
