// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalOptions.cs); unchanged apart from the namespace.
namespace Ch27.Beacon.Agent.Security.Approvals;

/// <summary>How long approvals stay answerable and how much a dispatcher may write with one.</summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Beacon:Approvals";

    /// <summary>How long a dispatcher has to answer. After that the approval can only be dismissed.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest reason a dispatcher may give for a denial or an edit.</summary>
    public int MaxReasonLength { get; set; } = 500;
}
