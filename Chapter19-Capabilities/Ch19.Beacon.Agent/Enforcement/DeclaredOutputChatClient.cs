using System.Runtime.CompilerServices;
using Ch19.Beacon.Agent.Capabilities;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Agent.Enforcement;

/// <summary>
/// Keeps the agent's output inside its declaration: when the deployment declares
/// <c>reasoning: { supported: false }</c>, reasoning content never reaches <c>AGUI.Server</c>, so no
/// <c>REASONING_*</c> event is written, whichever model sits underneath.
/// </summary>
public sealed class DeclaredOutputChatClient(IChatClient innerClient, BeaconDeployment deployment) : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (!deployment.Reasons && update.Contents.Any(c => c is TextReasoningContent))
            {
                update.Contents = [.. update.Contents.Where(c => c is not TextReasoningContent)];
                if (update.Contents.Count == 0)
                {
                    continue; // the update carried nothing but reasoning
                }
            }

            yield return update;
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (!deployment.Reasons)
        {
            foreach (var message in response.Messages)
            {
                message.Contents = [.. message.Contents.Where(c => c is not TextReasoningContent)];
            }
        }

        return response;
    }
}
