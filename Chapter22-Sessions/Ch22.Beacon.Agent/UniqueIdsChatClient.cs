using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Agent;

/// <summary>
/// Gives every message and tool call the model produces an ID that is unique for the life of the conversation.
/// </summary>
/// <remarks>
/// <c>AGUI.Server</c> uses the update's <c>MessageId</c> as the AG-UI message ID and the model's call ID as the
/// tool call's, so uniqueness is the provider's job, and a provider only promises it within a response. Once a
/// store keeps conversations, a repeated ID merges two messages: the scripted model starts again at
/// <c>msg-0001</c> after every restart, and a regenerated answer must not reuse the ID of the answer it
/// replaces. So the server mints its own. Place this layer right above the model, below function invocation,
/// so the call IDs that function invocation pairs with results are already the server's.
/// </remarks>
public sealed class UniqueIdsChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // One mapping per model call: every update of a message keeps the same new ID.
        Dictionary<string, string> messageIds = new(StringComparer.Ordinal);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (update.MessageId is { Length: > 0 } original)
            {
                update.MessageId = messageIds.TryGetValue(original, out var minted) ? minted : messageIds[original] = NewId("msg");
            }

            if (update.Contents.Any(c => c is FunctionCallContent))
            {
                update.Contents = [.. update.Contents.Select(c => c is FunctionCallContent call ? WithNewId(call) : c)];
            }

            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    private static FunctionCallContent WithNewId(FunctionCallContent call) =>
        new(NewId("call"), call.Name, call.Arguments)
        {
            InformationalOnly = call.InformationalOnly,
            RawRepresentation = call.RawRepresentation,
            AdditionalProperties = call.AdditionalProperties,
        };

    // 96 random bits: short enough to read in a wire listing, long enough never to repeat.
    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 1 + 24)];
}
