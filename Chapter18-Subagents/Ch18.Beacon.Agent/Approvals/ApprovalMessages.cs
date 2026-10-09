// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>
/// The approval exchange FunctionInvokingChatClient needs to resume, built from the server's records:
/// the request with the call as the model proposed it, and the answer with the call as approved (edited,
/// if it was) and the dispatcher's reason. Nothing in it comes from the client's resume payload except
/// the yes or no, the reason and an edit the gate has already checked.
/// </summary>
public static class ApprovalMessages
{
    public static IEnumerable<ChatMessage> For(IReadOnlyList<PendingApproval> decided)
    {
        if (decided.Count == 0)
        {
            yield break;
        }

        yield return new ChatMessage(ChatRole.Assistant,
            [.. decided.Select(a => (AIContent)new ToolApprovalRequestContent(a.Id, Call(a, a.Arguments.Clone())))]);

        yield return new ChatMessage(ChatRole.User,
            [.. decided.Select(a => (AIContent)new ToolApprovalResponseContent(a.Id, a.Status == ApprovalStatus.Approved, Call(a, a.ApprovedArguments.Clone()))
            {
                // FunctionInvokingChatClient puts a rejection's reason in the tool result the model reads.
                Reason = a.Status switch
                {
                    ApprovalStatus.Denied when a.Decision?.Reason is { } reason => $"The dispatcher declined it: {reason}",
                    ApprovalStatus.Denied => "The dispatcher declined it.",
                    ApprovalStatus.Cancelled => "The dispatcher dismissed the request without deciding.",
                    _ => null,
                },
            })]);
    }

    private static FunctionCallContent Call(PendingApproval approval, System.Text.Json.JsonElement arguments) =>
        new(approval.ToolCallId, approval.ToolName, ToolArguments.ToDictionary(arguments));
}
