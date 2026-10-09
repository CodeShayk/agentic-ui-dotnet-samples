using System.Text.Json;
using Beacon.AI;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Agent.Approvals;

/// <summary>
/// Wraps a consequential tool so it runs only when the spending policy allows it: within the limit, or
/// with an approval this server recorded, decided in this run, for exactly this call. Every path to the
/// tool passes through here, including paths the approval flow never sees, such as the SDK approving a
/// call on its own after a frontend tool's result comes back.
/// </summary>
public sealed class ApprovalCheckedFunction(
    AIFunction innerFunction,
    SpendingApprovalPolicy policy,
    IApprovalStore approvals,
    IApprovalAudit audit,
    TimeProvider time) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The call being invoked, and the run it belongs to, as FunctionInvokingChatClient sees them.
        var context = FunctionInvokingChatClient.CurrentContext;
        var run = ApprovalRun.From(context?.Options);
        var call = context?.CallContent;
        var json = ToolArguments.ToJson(arguments);

        // 1. A dispatcher approved exactly this call in this run. Spending the approval is single use.
        if (run is not null && call is not null && approvals.TryConsume(run.ThreadId, run.RunId, call.CallId, Name, json) is { } approval)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Executed, approval, time.GetUtcNow(), run.RunId, run.ClientId) with
            {
                Result = ToolResults.Describe(result),
            });
            return result;
        }

        // 2. No approval: the call may still be within the limit. The policy prices it again, from the catalog.
        var assessment = policy.Assess(Name, json);
        if (assessment.Need == ApprovalNeed.WithinLimit)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            Record(ApprovalEvent.Executed, assessment, "within the limit", ToolResults.Describe(result));
            return result;
        }

        // 3. Anything else stops here. The model learns the action didn't happen, and why in one sentence.
        var refusal = assessment.Need == ApprovalNeed.Invalid
            ? assessment.Summary
            : "This was not approved by a dispatcher, so it was not carried out.";
        Record(ApprovalEvent.Blocked, assessment, refusal, result: null);
        return JsonSerializer.SerializeToElement(new ToolError(refusal), BeaconJson.Options.GetTypeInfo(typeof(ToolError)));

        void Record(ApprovalEvent kind, SpendingAssessment priced, string reason, string? result) =>
            audit.Record(new ApprovalAuditEntry(
                time.GetUtcNow(),
                kind,
                run?.ThreadId ?? "-",
                run?.RunId ?? "-",
                run?.ClientId ?? BeaconClient.Unidentified,
                ToolName: Name,
                ToolCallId: call?.CallId,
                Arguments: json.GetRawText(),
                Amount: priced.Need == ApprovalNeed.Invalid ? null : priced.Amount,
                Reason: reason,
                Result: result));
    }
}
