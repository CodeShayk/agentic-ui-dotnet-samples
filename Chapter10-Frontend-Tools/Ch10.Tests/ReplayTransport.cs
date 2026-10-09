// Copied from Chapter 7 (Ch07.Tests); each chapter's code is a self-contained checkpoint.
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch10.Tests;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers each run with the next recorded event stream, so the
/// real AGUIChatClient, the WireEventTap above it and the conversation service run unchanged.
/// </summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private int _next;

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>When true, the first run's stream stays open after its last event until the run is cancelled, like a slow model.</summary>
    public bool HoldOpen { get; init; }

    /// <summary>Completes when a run's cancellation token fires: the client stopped the run.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the last event of a held-open run has been handed to the client.</summary>
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());

        if (_next >= runs.Length)
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        var run = _next++;
        foreach (var evt in runs[run])
        {
            await Task.Yield();
            yield return evt;
        }

        if (HoldOpen && run == 0)
        {
            Drained.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
