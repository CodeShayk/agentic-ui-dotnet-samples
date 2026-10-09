// Chapter 14's check at the tool (Ch14.Beacon.Agent/Approvals/ApprovalCheckedFunction.cs), spending a
// per-request grant for an authenticated user instead of looking an approval up by the client's run ID.
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch27.Beacon.Agent.Security.Logging;
using Ch27.Beacon.Agent.Security.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch27.Beacon.Agent.Security.Approvals;

/// <summary>
/// Wraps a consequential tool so it runs only within the spending limit or by spending a grant this request
/// holds for exactly this call. Every path to the tool passes through here, including the SDK approving a
/// call on its own after a frontend tool's result (Chapter 14).
/// </summary>
public sealed class GrantRequiredAIFunction(
    AIFunction innerFunction,
    SpendingApprovalPolicy policy,
    IApprovalStore approvals,
    ApprovalGrants grants,
    ApprovalRun run,
    IApprovalAudit audit,
    TimeProvider time,
    ILogger logger) : DelegatingAIFunction(innerFunction)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The call being invoked, as FunctionInvokingChatClient sees it.
        var call = FunctionInvokingChatClient.CurrentContext?.CallContent;
        var json = ToolArguments.ToJson(arguments);

        // 1. This request holds a grant for exactly this call, and the store agrees it is this user's and unspent.
        if (call is not null
            && grants.Find(call.CallId, Name, json) is { } granted
            && approvals.TryConsume(granted.Id, run.UserId, run.ThreadId, call.CallId, Name, json) is { } approval)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            audit.Record(ApprovalAuditEntry.For(ApprovalEvent.Executed, approval, time.GetUtcNow(), run) with { Result = ToolResults.Describe(result) });
            return result;
        }

        // 2. No grant: within the limit, the call runs. The policy prices it again, from the catalog.
        var assessment = policy.Assess(Name, json);
        if (assessment.Need == ApprovalNeed.WithinLimit)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            Record(ApprovalEvent.Executed, assessment, "within the limit", ToolResults.Describe(result));
            return result;
        }

        // 3. Anything else stops here. Over the limit, a technician learns who can help; anyone else that
        //    nobody approved it.
        var refusal = assessment.Need switch
        {
            ApprovalNeed.Invalid => assessment.Summary,
            _ when !run.Caller.IsDispatcher =>
                $"Spending over {SpendingApprovalPolicy.Money(SpendingPolicy.ApprovalThreshold)} needs a dispatcher. Ask dispatch to place this one.",
            _ => "This was not approved by a dispatcher, so it was not carried out.",
        };

        if (assessment.Need == ApprovalNeed.Required)
        {
            SecurityLog.BlockedUngrantedCall(logger, Name, call?.CallId ?? "(none)", run.UserId);
        }

        Record(ApprovalEvent.Blocked, assessment, refusal, result: null);
        return JsonSerializer.SerializeToElement(new ToolError(refusal), CallerScopedTools.Json.GetTypeInfo(typeof(ToolError)));

        void Record(ApprovalEvent kind, SpendingAssessment priced, string reason, string? result) =>
            audit.Record(new ApprovalAuditEntry(
                time.GetUtcNow(),
                kind,
                run.ThreadId,
                run.RunId,
                run.UserId,
                run.ClientId,
                ToolName: Name,
                ToolCallId: call?.CallId,
                Arguments: json.GetRawText(),
                Amount: priced.Need == ApprovalNeed.Invalid ? null : priced.Amount,
                Reason: reason,
                Result: result));
    }
}
