// Adapted from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalPolicyChatClient.cs). Chapter 23 asks
// only on a dispatcher's run, because only a dispatcher may approve.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch23.Beacon.Security.Approvals;

/// <summary>
/// Conditional approval, between FunctionInvokingChatClient and the model. A call the spending policy says
/// needs a dispatcher becomes a <see cref="ToolApprovalRequestContent"/> under an ID the server issues and
/// records for this user. On a technician's run nothing is proposed: nobody on that thread could approve
/// it, so the call goes on to the check at the tool, which refuses it.
/// </summary>
public sealed class ApprovalPolicyChatClient(IChatClient innerClient, SpendingApprovalPolicy policy, IApprovalStore approvals)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = ApprovalRun.From(options);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (run is { Caller.IsDispatcher: true } && update.Contents.Any(c => c is FunctionCallContent))
            {
                update.Contents = [.. update.Contents.Select(content => Gate(content, run))];
            }

            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    private AIContent Gate(AIContent content, ApprovalRun run)
    {
        if (content is not FunctionCallContent call
            || policy.Assess(call.Name, ToolArguments.ToJson(call.Arguments)) is not { Need: ApprovalNeed.Required } assessment)
        {
            return content;
        }

        // The server's record comes first; the request the client will see carries its ID.
        var approval = approvals.Propose(run, call, assessment);
        return new ToolApprovalRequestContent(approval.Id, call);
    }
}
