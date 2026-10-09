// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>
/// Conditional approval. <see cref="ApprovalRequiredAIFunction"/> is all or nothing: FunctionInvokingChatClient
/// asks about every call to the tool, whatever its arguments. This layer sits between
/// FunctionInvokingChatClient and the model, so it sees each call the model makes first. A call the
/// spending policy says needs a dispatcher becomes a <see cref="ToolApprovalRequestContent"/> under an
/// ID the server issues and records; FunctionInvokingChatClient never sees it as a call, so it can't run it.
/// Calls within the limit pass through untouched.
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
            if (run is not null && update.Contents.Any(c => c is FunctionCallContent))
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
