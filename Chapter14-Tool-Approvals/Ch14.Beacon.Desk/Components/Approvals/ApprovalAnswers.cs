using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AGUI.Abstractions;
using Beacon.Client.Core;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Desk.Components.Approvals;

/// <summary>What the dispatcher chose in the dialog.</summary>
public enum ApprovalChoice
{
    Approve,
    Deny,
    Edit,

    /// <summary>Abandon an approval that expired, so the thread can move on.</summary>
    Dismiss,
}

/// <param name="Choice">The decision.</param>
/// <param name="Reason">Why, for a denial or an edit. Beacon and the audit log both see it.</param>
/// <param name="EditedArguments">For an edit, the full replacement arguments.</param>
public sealed record ApprovalDecision(ApprovalChoice Choice, string? Reason = null, JsonObject? EditedArguments = null);

/// <summary>Sends a dispatcher's decision to the agent through the conversation.</summary>
public static class ApprovalAnswers
{
    public static Task SendAsync(AgentConversation conversation, ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken = default) =>
        decision.Choice == ApprovalChoice.Approve
            // A plain yes is what the SDK's types carry: RespondAsync puts the request and this response at
            // the end of the history, and AGUIChatClient turns the pair into the run's resume entry.
            ? conversation.RespondAsync([Approve(request)], cancellationToken: cancellationToken)
            // A reason or an edit is more than ToolApprovalResponseContent takes to the wire, so the Desk
            // writes the resume entry itself, in the shape the interrupt's responseSchema describes.
            : conversation.ContinueAsync(new RunInputs { Resume = [ToResume(request, decision)] }, cancellationToken);

    public static ToolApprovalResponseContent Approve(ApprovalRequest request) =>
        request.Request is ToolApprovalRequestContent approval
            ? approval.CreateResponse(approved: true)
            : new ToolApprovalResponseContent(request.Id, approved: true,
                new FunctionCallContent(request.ToolCallId, request.ToolName,
                    request.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())));

    public static AGUIResume ToResume(ApprovalRequest request, ApprovalDecision decision)
    {
        if (decision.Choice == ApprovalChoice.Dismiss)
        {
            return new AGUIResume { InterruptId = request.Id, Status = ResumeStatus.Cancelled };
        }

        var payload = new JsonObject { ["approved"] = decision.Choice != ApprovalChoice.Deny };
        if (!string.IsNullOrWhiteSpace(decision.Reason))
        {
            // The agent refuses control characters in a reason, which goes into its audit log, so the line breaks
            // a dispatcher types in the reason box become spaces.
            payload["reason"] = Regex.Replace(decision.Reason.Trim(), @"\p{Cc}+", " ");
        }

        if (decision is { Choice: ApprovalChoice.Edit, EditedArguments: { } edited })
        {
            payload["editedArgs"] = edited.DeepClone();
        }

        return new AGUIResume
        {
            InterruptId = request.Id,
            Status = ResumeStatus.Resolved,
            Payload = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
        };
    }
}
