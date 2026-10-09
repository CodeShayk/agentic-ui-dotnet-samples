// Copied from Chapter 23 (Ch23.Beacon.Security). The run budget moved to RunGovernor (Beacon:Hosting).
namespace Ch27.Beacon.Agent.Security;

/// <summary>Budgets for runs and callers, bound from <c>Beacon:Security</c>. Approvals keep Chapter 14's <c>Beacon:Approvals</c>.</summary>
public sealed class BeaconSecurityOptions
{
    public const string SectionName = "Beacon:Security";

    /// <summary>Runs one authenticated user may start per minute.</summary>
    public int RunsPerMinute { get; set; } = 30;
}
