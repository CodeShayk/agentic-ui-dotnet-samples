// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals); each chapter's code is a self-contained checkpoint.
namespace Ch18.Beacon.Agent.Approvals;

/// <summary>How long approvals stay answerable and how much a dispatcher may write with one.</summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Beacon:Approvals";

    /// <summary>How long a dispatcher has to answer. After that the approval can only be dismissed.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest reason a dispatcher may give for a denial or an edit.</summary>
    public int MaxReasonLength { get; set; } = 500;
}
