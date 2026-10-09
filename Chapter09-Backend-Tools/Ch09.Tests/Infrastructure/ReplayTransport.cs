using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch09.Tests.Infrastructure;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers a run with a recorded event stream, so the real
/// WireEventTap, AGUIChatClient and AgentConversation above it run unchanged. A gated transport holds
/// each event until the test releases it, which makes "what is on screen after the third event" a
/// question with one answer.
/// </summary>
public sealed class ReplayTransport(IReadOnlyList<BaseEvent> events) : IAGUITransport
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _planned;

    /// <summary>When true, each event waits for <see cref="ReleaseThrough"/> or <see cref="ReleaseAll"/>.</summary>
    public bool Gated { get; init; }

    /// <summary>When true, the stream stays open after the last event until the run is cancelled.</summary>
    public bool HoldOpen { get; init; }

    /// <summary>When set, the run fails with this exception before any event, as a refused request does.</summary>
    public Exception? Throws { get; init; }

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>Completes when a run's cancellation token fires, that is, when the client stops the run.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
        if (Throws is not null)
        {
            await Task.Yield();
            throw Throws;
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

    /// <summary>Releases everything that is left.</summary>
    public void ReleaseAll() => ReleaseThrough(_ => false);

    // A real server echoes the client's thread and run IDs; the recording carries the capture's.
    private static BaseEvent WithRunIds(BaseEvent evt, RunAgentInput input) => evt switch
    {
        RunStartedEvent => new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId },
        RunFinishedEvent finished => new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId, Outcome = finished.Outcome },
        _ => evt,
    };
}
