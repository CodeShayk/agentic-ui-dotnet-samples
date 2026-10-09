using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch06.Tests;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers every run with a recorded event stream. It stands in
/// for the network, so the real AGUIChatClient (and everything above it) runs unchanged. A gated
/// transport holds each event until the test releases it, which makes "what is on screen after the
/// third event" a question with one answer.
/// </summary>
public sealed class ReplayTransport(IReadOnlyList<BaseEvent> events) : IAGUITransport
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _planned;

    /// <summary>When true, each event waits for <see cref="ReleaseThrough"/> or <see cref="ReleaseAll"/>.</summary>
    public bool Gated { get; init; }

    /// <summary>When true, the stream stays open after the last event until the run is canceled.</summary>
    public bool HoldOpen { get; init; }

    /// <summary>When set, the first run fails with this exception before any event, as a request can before its headers arrive.</summary>
    public Exception? FailFirstRunWith { get; init; }

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>Completes when a run's cancellation token fires, that is, when the client stops the run.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<BaseEvent> SendAsync(
        RunAgentInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());

        if (FailFirstRunWith is { } failure && Inputs.Count == 1)
        {
            await Task.Yield();
            throw failure;
        }

        foreach (var evt in events)
        {
            if (Gated)
            {
                await _gate.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            yield return WithRunIds(evt, input);
        }

        if (HoldOpen)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Releases events up to and including the next one that matches.</summary>
    public void ReleaseThrough(Func<BaseEvent, bool> match)
    {
        var count = 0;
        while (_planned < events.Count)
        {
            count++;
            if (match(events[_planned++]))
            {
                break;
            }
        }

        _gate.Release(count);
    }

    public void ReleaseAll() => ReleaseThrough(_ => false);

    // A real server echoes the client's thread and run IDs; the recording carries the IDs of the capture.
    private static BaseEvent WithRunIds(BaseEvent evt, RunAgentInput input) => evt switch
    {
        RunStartedEvent => new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId },
        RunFinishedEvent finished => new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId, Outcome = finished.Outcome },
        _ => evt,
    };
}
