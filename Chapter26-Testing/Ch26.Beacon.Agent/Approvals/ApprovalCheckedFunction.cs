using System.Text.Json;
using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Approvals;

/// <summary>
/// The check at the tool (Chapter 14): a call over the limit runs only when the ledger holds the
/// server's own request for exactly this call on this thread. Every path to the tool passes through
/// here, including a forged resume entry that the SDK would otherwise treat as approved.
/// </summary>
public sealed class ApprovalCheckedFunction(AIFunction innerFunction, IFieldServiceStore store, ApprovalLedger ledger)
    : DelegatingAIFunction(innerFunction)
{
    public const string NotApproved = "A dispatcher has not approved this, so it was not carried out.";
    public const string NotPriced = "I couldn't price this from the catalog, so it was not carried out.";

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        switch (SpendingCheck.Assess(store, Name, arguments))
        {
            case SpendingVerdict.Unpriceable:
                return Refuse(NotPriced);

            case SpendingVerdict.NeedsApproval:
                var context = FunctionInvokingChatClient.CurrentContext;
                var threadId = context?.Options is { } options && options.TryGetRunAgentInput(out var input) ? input.ThreadId : null;
                if (context?.CallContent is not { } call || threadId is null || !ledger.TryConsume(threadId, call))
                {
                    return Refuse(NotApproved);
                }

                break;
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement Refuse(string reason) =>
        JsonSerializer.SerializeToElement(new ToolError(reason), BeaconJson.Options.GetTypeInfo(typeof(ToolError)));
}
