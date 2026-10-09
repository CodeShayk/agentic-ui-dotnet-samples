using System.Text.Json;

namespace Ch23.Beacon.Security.Approvals;

/// <summary>
/// The approvals one request may spend. The reconciler builds it from the answers its gate accepted, and the
/// handler gives it to the tools it builds for that request and no other. Chapter 14 ties spending to the run
/// ID that carried the answer, a value the client writes; a grant is an object nothing outside this request
/// can reach, and every approval in it belongs to the authenticated caller.
/// </summary>
public sealed class ApprovalGrants(IEnumerable<PendingApproval> decided)
{
    private readonly IReadOnlyList<PendingApproval> _approved = [.. decided.Where(a => a.Status == ApprovalStatus.Approved)];

    public static ApprovalGrants None { get; } = new([]);

    public int Count => _approved.Count;

    /// <summary>The approval granted for exactly this call: same call ID, tool and approved arguments.</summary>
    public PendingApproval? Find(string toolCallId, string toolName, JsonElement arguments) =>
        _approved.FirstOrDefault(a => a.ToolCallId == toolCallId && a.ToolName == toolName && ToolArguments.Same(a.ApprovedArguments, arguments));
}
