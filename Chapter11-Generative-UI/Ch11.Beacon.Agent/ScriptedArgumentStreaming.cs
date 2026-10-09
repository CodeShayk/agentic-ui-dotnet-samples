// Copied from Chapter 9 (Ch09.Beacon.Agent); each chapter's code is a self-contained checkpoint.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent;

/// <summary>
/// One piece of a tool call's arguments, as a streaming model provider sends it: the first piece of a
/// call carries the call's ID and the function's name, later pieces only its index and the next text.
/// </summary>
public sealed record ScriptedArgumentFragment(int Index, string? ToolCallId, string? FunctionName, string Delta);

/// <summary>
/// Makes Beacon's scripted model stream tool-call arguments the way OpenAI-style providers do.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI's OpenAI client passes every chunk on as an update whose
/// <see cref="ChatResponseUpdate.RawRepresentation"/> is the provider's own chunk, and reports the
/// complete calls (<see cref="FunctionCallContent"/>) together, after the last chunk. The scripted model
/// sends each call whole, so this layer cuts each call's arguments into fragments, sends them at once as
/// content-less updates carrying a <see cref="ScriptedArgumentFragment"/>, and holds the complete calls
/// back until the model's turn ends. Put it directly in front of the scripted model.
/// </remarks>
public sealed class ScriptedArgumentStreaming(IChatClient innerClient, TimeSpan fragmentDelay, int fragmentLength = 6)
    : DelegatingChatClient(innerClient)
{
    // Compact, like the arguments AGUI.Server writes for a call that arrives whole.
    private static readonly JsonSerializerOptions ArgumentOptions = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> completeCalls = [];
        var index = 0; // like a provider's tool-call index: it restarts with every response

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (!update.Contents.OfType<FunctionCallContent>().Any())
            {
                yield return update; // text and reasoning stream as they are
                continue;
            }

            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                var arguments = JsonSerializer.Serialize(call.Arguments, ArgumentOptions.GetTypeInfo(typeof(IDictionary<string, object?>)));
                for (var offset = 0; offset < arguments.Length; offset += fragmentLength)
                {
                    await Task.Delay(fragmentDelay, cancellationToken);
                    var first = offset == 0;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [])
                    {
                        ResponseId = update.ResponseId,
                        MessageId = update.MessageId,
                        ModelId = update.ModelId,
                        RawRepresentation = new ScriptedArgumentFragment(
                            index,
                            first ? call.CallId : null,
                            first ? call.Name : null,
                            arguments.Substring(offset, Math.Min(fragmentLength, arguments.Length - offset))),
                    };
                }

                index++;
            }

            completeCalls.Add(update);
        }

        // The complete calls come last, as Microsoft.Extensions.AI's providers report them.
        foreach (var update in completeCalls)
        {
            yield return update;
        }
    }
}
