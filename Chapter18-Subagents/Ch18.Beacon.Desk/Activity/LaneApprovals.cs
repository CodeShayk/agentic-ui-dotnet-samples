using System.Text.Json;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Desk.Activity;

/// <summary>
/// Answers an approval a specialist is waiting on. The answer goes to the run as a whole, like any other:
/// the client resumes the thread, and the coordinator hands the decision to the invocation that asked.
/// </summary>
public static class LaneApprovals
{
    /// <summary>A plain yes, as Chapter 14 sends it: the request and the response at the end of the history.</summary>
    public static Task ApproveAsync(AgentConversation conversation, InterruptView interrupt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(interrupt);

        var response = interrupt.Request is ToolApprovalRequestContent request
            ? request.CreateResponse(approved: true)
            : new ToolApprovalResponseContent(interrupt.Id, approved: true, CallOf(conversation.View, interrupt));
        return conversation.RespondAsync([response], cancellationToken: cancellationToken);
    }

    /// <summary>A no, written as the resume entry itself, in the shape the interrupt's schema describes.</summary>
    public static Task DeclineAsync(AgentConversation conversation, InterruptView interrupt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(interrupt);

        var resume = new AGUIResume
        {
            InterruptId = interrupt.Id,
            Status = ResumeStatus.Resolved,
            Payload = JsonDocument.Parse("""{"approved":false,"reason":"Declined from Beacon Desk."}""").RootElement.Clone(),
        };
        return conversation.ContinueAsync(new RunInputs { Resume = [resume] }, cancellationToken);
    }

    private static FunctionCallContent CallOf(ConversationView view, InterruptView interrupt) =>
        view.FindToolCall(interrupt.ToolCallId ?? string.Empty) is { } call
            ? new FunctionCallContent(call.Id, call.Name,
                call.Arguments is { ValueKind: JsonValueKind.Object } args
                    ? args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                    : null)
            : throw new InvalidOperationException($"Interrupt {interrupt.Id} names no tool call this client has seen.");
}
