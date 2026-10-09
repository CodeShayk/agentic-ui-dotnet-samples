using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch07.Tests;

/// <summary>
/// An <see cref="IAGUITransport"/> that answers each run with the next recorded event stream, so the
/// real AGUIChatClient, the WireEventTap above it and the conversation service run unchanged.
/// </summary>
public sealed class ReplayTransport(params IReadOnlyList<BaseEvent>[] runs) : IAGUITransport
{
    private int _next;

    /// <summary>Every RunAgentInput the client sent, in order.</summary>
    public List<RunAgentInput> Inputs { get; } = [];

    /// <summary>When true, the first run's stream stays open after its last event until the run is canceled, like a slow model.</summary>
    public bool HoldOpen { get; init; }

    /// <summary>Completes when a run's cancellation token fires: the client stopped the run.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the last event of a held-open run has been handed to the client.</summary>
    public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs, by index, that fail before their first event: a refusal such as an HTTP 409.</summary>
    public Dictionary<int, Exception> FailBefore { get; } = [];

    /// <summary>Runs, by index, that fail after their last event: a connection that broke mid-stream.</summary>
    public Dictionary<int, Exception> FailAfter { get; } = [];

    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());

        if (_next >= runs.Length)
        {
            throw new InvalidOperationException($"No recorded run left for run {Inputs.Count}.");
        }

        var run = _next++;
        if (FailBefore.TryGetValue(run, out var refusal))
        {
            throw refusal;
        }

        foreach (var evt in runs[run])
        {
            await Task.Yield();
            yield return evt;
        }

        if (FailAfter.TryGetValue(run, out var broken))
        {
            throw broken;
        }

        if (HoldOpen && run == 0)
        {
            Drained.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
