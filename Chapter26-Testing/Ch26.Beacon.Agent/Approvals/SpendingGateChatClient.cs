using System.Runtime.CompilerServices;
using AGUI.Server;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Approvals;

/// <summary>
/// Conditional approval, as in Chapter 14. This layer sits between FunctionInvokingChatClient and the
/// model, so it sees each call the model makes first. A call the spending rule says needs a dispatcher
/// becomes a <see cref="ToolApprovalRequestContent"/> under an ID the ledger issues; the AG-UI converter
/// turns it into a <c>RUN_FINISHED</c> interrupt. Calls within the limit pass through untouched.
/// </summary>
public sealed class SpendingGateChatClient(IChatClient innerClient, IFieldServiceStore store, ApprovalLedger ledger)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var threadId = options is not null && options.TryGetRunAgentInput(out var input) ? input.ThreadId : null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (threadId is not null && update.Contents.Any(c => c is FunctionCallContent))
            {
                update.Contents = [.. update.Contents.Select(content => Gate(content, threadId))];
            }

            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    private AIContent Gate(AIContent content, string threadId)
    {
        if (content is not FunctionCallContent call
            || SpendingCheck.Assess(store, call.Name, call.Arguments) != SpendingVerdict.NeedsApproval)
        {
            return content; // within the limit, not gated, or unpriceable (which the tool refuses)
        }

        // The server's record comes first; the request the client sees carries its ID.
        var amount = SpendingCheck.Price(store, call.Name, call.Arguments)!.Value;
        return new ToolApprovalRequestContent(ledger.Propose(threadId, call, amount).Id, call);
    }
}
