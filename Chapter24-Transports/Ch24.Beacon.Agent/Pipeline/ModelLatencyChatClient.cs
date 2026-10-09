using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch24.Beacon.Agent.Pipeline;

/// <summary>
/// Makes the scripted model take its time before the first chunk of every reply, the way a hosted
/// model does, and a reasoning model thinking silently does for much longer. It stands in for a real
/// provider's time to first token when measuring streams and testing timeouts; it is never part of
/// a production pipeline.
/// </summary>
public sealed class ModelLatencyChatClient(IChatClient innerClient, TimeSpan firstChunkDelay, TimeProvider? timeProvider = null)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (firstChunkDelay > TimeSpan.Zero)
        {
            await Task.Delay(firstChunkDelay, timeProvider ?? TimeProvider.System, cancellationToken);
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }
}
