using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch27.Beacon.Agent.Hosting;

/// <summary>
/// Just under Chapter 24's early <c>RUN_STARTED</c>, the outermost layer that runs the model and tools. It runs
/// them under the run's budget and the replica's drain deadline. When either one stops a run whose client is still listening, it
/// ends the stream with a <c>RUN_ERROR</c> that says why, rather than leaving a proxy to cut the
/// connection or the SDK to report a generic failure.
/// </summary>
public sealed class GovernedChatClient(IChatClient innerClient, RunGovernor governor) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var run = governor.BeginRun(cancellationToken);
        return await base.GetResponseAsync(messages, options, run.Token);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var run = governor.BeginRun(cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(messages, options, run.Token).GetAsyncEnumerator(run.Token);

        var stoppedByServer = false;
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && run.StopCode is not null)
            {
                // The budget or the drain fired, not the client. Someone is still listening.
                stoppedByServer = true;
                break;
            }

            yield return updates.Current;
        }

        if (stoppedByServer)
        {
            // An AG-UI event in RawRepresentation is written verbatim (Chapter 4): RUN_ERROR with a code.
            yield return new ChatResponseUpdate { RawRepresentation = run.CreateError() };
        }
    }
}
