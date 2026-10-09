using Beacon.AI;
using Beacon.Domain;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Agent.Approvals;

/// <summary>
/// Beacon's server tools for this chapter: the six read-only ones as they are, and the two that spend
/// money behind the spending check.
/// </summary>
public sealed class BeaconToolset
{
    public BeaconToolset(IFieldServiceStore store, ApprovalLedger ledger)
    {
        var tools = new BeaconTools(store);
        All =
        [
            .. tools.CreateReadOnlyTools(),
            .. tools.CreateConsequentialTools().Select(tool => new ApprovalCheckedFunction(tool, store, ledger)),
        ];
        Names = All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<AITool> All { get; }

    public IReadOnlySet<string> Names { get; }
}
