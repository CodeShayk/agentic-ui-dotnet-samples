// Copied from Chapter 19 (Ch19.Beacon.Agent/Versioning/RunStartedDeclarationChatClient.cs); only the namespace and the last remark changed.
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.AgentFramework;

/// <summary>
/// Declares the protocol version for an agent hosted with Agent Framework's <c>MapAGUIServer</c>, which has no
/// option for it. <c>AGUI.Server</c> writes a <see cref="RunStartedEvent"/> that arrives in an update's
/// <see cref="ChatResponseUpdate.RawRepresentation"/> instead of its own, so this layer opens every run with
/// one that carries <see cref="AGUIProtocol.Version"/>. It must be the outermost layer of the agent's chat
/// pipeline, above function invocation: below it, each model call would open the run again.
/// (This chapter's own agent stamps its stream with <c>WithProtocolVersion</c> instead.)
/// </summary>
public sealed class RunStartedDeclarationChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The original input is on the options, behind MapAGUIServer as behind the book's glue.
        RunAgentInput? input = null;
        if (options is not null && options.TryGetRunAgentInput(out input))
        {
            yield return new ChatResponseUpdate
            {
                RawRepresentation = new RunStartedEvent
                {
                    ThreadId = input.ThreadId,
                    RunId = input.RunId,
                    ParentRunId = input.ParentRunId,
                    ProtocolVersion = AGUIProtocol.Version,
                },
            };
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
