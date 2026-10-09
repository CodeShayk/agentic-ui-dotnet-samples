using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Client;

namespace Ch08.Field.Core.Connection;

/// <summary>
/// An <see cref="IAGUITransport"/> for bad networks. Until a run's first event arrives, a failure that
/// shows the run never started is retried with the same input (the same run ID). From the first
/// event on, the run belongs to the server: a broken stream is reported as a truncated run and never
/// sent again, because the agent may already have acted on it.
/// </summary>
/// <param name="inner">The transport that talks to the agent, normally the SDK's HTTP transport.</param>
/// <param name="policy">How many attempts, how far apart and which failures qualify.</param>
public sealed class ResilientAgentTransport(IAGUITransport inner, ConnectionPolicy? policy = null) : IAGUITransport
{
    private readonly ConnectionPolicy _policy = policy ?? ConnectionPolicy.Default;

    /// <summary>Raised on the reading thread for every stage of every run's connection. Keep handlers short.</summary>
    public event EventHandler<ConnectionEvent>? ConnectionChanged;

    /// <inheritdoc />
    public async IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Open the run: retry until the first event arrives, or give up.
        var (events, attempt) = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        if (events is null)
        {
            yield break;
        }

        // 2. The run has started. Pass every event through; a break from here on is a truncated run.
        await using (events.ConfigureAwait(false))
        {
            var received = 0;
            var sawTerminal = false;
            while (true)
            {
                var current = events.Current;
                received++;
                sawTerminal = current is RunFinishedEvent or RunErrorEvent;
                yield return current;

                bool more;
                try
                {
                    more = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    // Stop, or the app went to the background: a cancellation, whatever the socket said.
                    Report(new(input.RunId, attempt, ConnectionStage.Cancelled) { EventsReceived = received });
                    throw AsCancellation(ex, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    Report(new(input.RunId, attempt, ConnectionStage.Truncated) { EventsReceived = received, Error = ex });
                    throw new TruncatedRunException(input.RunId, received, current.Type, ex);
                }

                if (!more)
                {
                    break;
                }
            }

            // A stream that closes cleanly without a terminal event is truncated too; AgentConversation
            // reports it as StreamEnded, so it only needs recording here.
            Report(new(input.RunId, attempt, sawTerminal ? ConnectionStage.Finished : ConnectionStage.Truncated) { EventsReceived = received });
        }
    }

    // Returns the stream positioned on its first event, or null if it ended without one.
    private async Task<(IAsyncEnumerator<BaseEvent>? Events, int Attempt)> OpenAsync(RunAgentInput input, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var events = inner.SendAsync(input, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                if (await events.MoveNextAsync().ConfigureAwait(false))
                {
                    Report(new(input.RunId, attempt, ConnectionStage.Started) { EventsReceived = 1 });
                    return (events, attempt);
                }

                await events.DisposeAsync().ConfigureAwait(false);
                Report(new(input.RunId, attempt, ConnectionStage.Truncated));
                return (null, attempt);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(events).ConfigureAwait(false);
                Report(new(input.RunId, attempt, ConnectionStage.Cancelled));
                throw AsCancellation(ex, cancellationToken);
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(events).ConfigureAwait(false);
                if (attempt >= _policy.MaxAttempts || !ConnectionPolicy.IsSafeToRetry(ex)
                    || _policy.DelayBefore(attempt + 1, ex) is not { } delay)
                {
                    Report(new(input.RunId, attempt, ConnectionStage.GaveUp) { Error = ex });
                    throw;
                }

                // The run never started, so sending it again can't repeat anything. The input goes again
                // unchanged, run ID included, no sooner than the server asked (Retry-After).
                Report(new(input.RunId, attempt, ConnectionStage.Retrying) { Error = ex, RetryDelay = delay });
                await Task.Delay(delay, _policy.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static OperationCanceledException AsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception as OperationCanceledException ?? new OperationCanceledException("The run was cancelled.", exception, cancellationToken);

    private void Report(ConnectionEvent connectionEvent) =>
        ConnectionChanged?.Invoke(this, connectionEvent with { MaxAttempts = _policy.MaxAttempts });

    private static async ValueTask DisposeQuietlyAsync(IAsyncEnumerator<BaseEvent> events)
    {
        try
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            // The attempt already failed; disposing what's left of it can't make that worse.
        }
    }
}
