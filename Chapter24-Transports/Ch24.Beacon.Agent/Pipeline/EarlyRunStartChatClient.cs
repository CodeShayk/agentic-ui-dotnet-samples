using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch24.Beacon.Agent.Pipeline;

/// <summary>
/// Opens an AG-UI run before the model is called. <c>AGUI.Server</c> emits <c>RUN_STARTED</c> only when
/// the model's first update arrives, so on its own the response headers and the first byte wait for
/// the model's time to first token. A <see cref="RunStartedEvent"/> in
/// <see cref="ChatResponseUpdate.RawRepresentation"/> is passed through verbatim, and the converter
/// then doesn't synthesize its own.
/// </summary>
/// <remarks>Place this layer first (outermost), so it runs once per run, not once per model call.</remarks>
public sealed class EarlyRunStartChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Only an AG-UI run has a run to open; any other caller gets the inner stream untouched.
        if (options is not null && options.TryGetRunAgentInput(out var input))
        {
            yield return new ChatResponseUpdate
            {
                RawRepresentation = new RunStartedEvent
                {
                    ThreadId = input.ThreadId,
                    RunId = input.RunId,
                    ParentRunId = input.ParentRunId,
                    ProtocolVersion = AGUIProtocol.Version, // we build this event, so it declares the version (Chapter 19)
                },
            };
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }
}
