using System.Runtime.CompilerServices;
using AGUI.Abstractions;

namespace Ch24.Beacon.Agent.Streaming;

/// <summary>What Beacon changes on events between the SDK's converter and the wire.</summary>
public static class WireShaping
{
    /// <summary>The code a run ends with when it outlives <see cref="StreamingOptions.RunTimeout"/>.</summary>
    public const string RunTimeoutCode = "RUN_TIMEOUT";

    /// <summary>
    /// Drops <c>rawEvent</c> when asked to, and relabels the converter's generic <c>RUN_ERROR</c> as a
    /// timeout when the run budget is what ended the run.
    /// </summary>
    public static async IAsyncEnumerable<BaseEvent> ShapeForWire(
        this IAsyncEnumerable<BaseEvent> events,
        bool stripRawEvent,
        CancellationToken runBudget,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken))
        {
            if (stripRawEvent)
            {
                // The SDK copies the source ChatResponseUpdate onto most events. Clients ignore it,
                // and it's most of the bytes.
                evt.RawEvent = null;
            }

            if (evt is RunErrorEvent error && runBudget.IsCancellationRequested)
            {
                error.Code = RunTimeoutCode;
                error.Message = "The run took longer than Beacon allows.";
            }

            yield return evt;
        }
    }
}
