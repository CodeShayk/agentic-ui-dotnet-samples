using System.Runtime.CompilerServices;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch06.Beacon.Agent;

/// <summary>
/// Logs one line when a run starts and one when it ends, with the AG-UI thread and run IDs and the
/// size of the history the client sent. It sits outermost in the pipeline, so "cancelled" means the
/// client went away (the Desk's Stop button) and the whole run, model call included, was abandoned.
/// </summary>
internal sealed class RunLogChatClient(IChatClient inner, ILogger<RunLogChatClient> logger) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyCollection<ChatMessage> ?? [.. messages];
        string? threadId = null, runId = null;
        if (options?.TryGetRunAgentInput(out var input) == true)
        {
            (threadId, runId) = (input.ThreadId, input.RunId);
        }

        logger.LogInformation("Run {RunId} started on thread {ThreadId}, history of {MessageCount} message(s)", runId, threadId, history.Count);

        var updates = 0;
        var completed = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(history, options, cancellationToken))
            {
                updates++;
                yield return update;
            }

            completed = true;
        }
        finally
        {
            if (completed)
            {
                logger.LogInformation("Run {RunId} finished after {UpdateCount} updates", runId, updates);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Run {RunId} cancelled by the client after {UpdateCount} updates", runId, updates);
            }
        }
    }
}
