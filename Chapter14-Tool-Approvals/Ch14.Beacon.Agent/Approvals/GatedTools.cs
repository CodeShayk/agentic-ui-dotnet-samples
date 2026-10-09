using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Agent.Approvals;

/// <summary>
/// Beacon's tools for this chapter: the six read-only ones as they are, and the two that spend money
/// behind the check at the tool. Built once; the check reads the run from the call's context.
/// </summary>
public sealed class GatedTools
{
    public GatedTools(IFieldServiceStore store, SpendingApprovalPolicy policy, IApprovalStore approvals, IApprovalAudit audit, TimeProvider time)
    {
        var tools = new BeaconTools(store);
        All =
        [
            .. tools.CreateReadOnlyTools(),
            .. tools.CreateConsequentialTools().Select(tool => new ApprovalCheckedFunction(tool, policy, approvals, audit, time)),
        ];
        Names = All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<AITool> All { get; }

    public IReadOnlySet<string> Names { get; }
}
