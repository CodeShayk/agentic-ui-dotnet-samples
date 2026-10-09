using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Ch07.Beacon.Agent;

/// <summary>
/// Ends every run by restating the whole conversation with <c>MESSAGES_SNAPSHOT</c>: the messages the
/// model saw, followed by the ones this run produced. An agent with server-side history does this to
/// give the client its authoritative view (Chapter 22 builds one); here it makes the client's
/// reconciliation run on every turn, so a mismatch in IDs or content shows up at once.
/// </summary>
/// <remarks>
/// A <see cref="BaseEvent"/> placed in <see cref="ChatResponseUpdate.RawRepresentation"/> is written
/// to the stream verbatim by <c>AsAGUIEventStreamAsync</c>.
/// </remarks>
internal sealed class MessagesSnapshotChatClient(IChatClient innerClient, JsonSerializerOptions jsonOptions)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> conversation = [.. messages];
        List<ChatResponseUpdate> updates = [];

        await foreach (var update in base.GetStreamingResponseAsync(conversation, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        // The run's output as messages: text, the tool calls and their results, keyed by the same
        // message IDs the events carried.
        conversation.AddMessages(updates);

        // Reasoning has no place in this restatement: AsAGUIMessages can't express it, and the
        // server converter minted its own IDs for the REASONING_* events anyway. A reasoning-only
        // message would come out as an empty assistant message, so it is left out, and the client
        // keeps its reasoning messages as material the producer doesn't track.
        var restated = conversation.Where(m => m.Role != ChatRole.Assistant || m.Contents.Any(c => c is TextContent or FunctionCallContent));

        yield return new ChatResponseUpdate
        {
            RawRepresentation = new MessagesSnapshotEvent { Messages = [.. restated.AsAGUIMessages(jsonOptions)] },
        };
    }
}
