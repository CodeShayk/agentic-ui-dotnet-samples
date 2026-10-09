using System.Runtime.CompilerServices;
using AGUI.Abstractions;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// The last stage before the wire. When the run finishes with interrupts, the requests behind them are
/// opened before the client reads <c>RUN_FINISHED</c>, so a client that answers at once finds them open.
/// Requests the client never saw (the run failed or was stopped) are withdrawn.
/// </summary>
public static class InputEventStream
{
    public static async IAsyncEnumerable<BaseEvent> RunAsync(
        IAsyncEnumerable<BaseEvent> events,
        InputRun run,
        IInputRequestStore inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (evt is RunFinishedEvent { Outcome: RunFinishedInterruptOutcome outcome })
                {
                    foreach (var proposed in inputs.ProposedIn(run.ThreadId, run.RunId))
                    {
                        if (outcome.Interrupts.Any(i => i.Id == proposed.Id))
                        {
                            inputs.Open(proposed);
                        }
                    }
                }

                yield return evt;
            }
        }
        finally
        {
            inputs.Withdraw(run.ThreadId, run.RunId);
        }
    }
}
