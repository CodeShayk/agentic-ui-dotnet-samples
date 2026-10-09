// Adapted from Chapter 14 (Ch14.Beacon.Agent/Approvals/ApprovalStore.cs). Chapter 23 keys every lookup
// by the authenticated user as well as the thread.
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch23.Beacon.Security.Approvals;

/// <summary>
/// The approvals the server has asked for. Every lookup names the user and the thread; every change is a
/// check-and-set on the current version, so an approval moves forward once.
/// </summary>
public interface IApprovalStore
{
    /// <summary>Records a gated call the model just proposed, under a new server-issued ID.</summary>
    PendingApproval Propose(ApprovalRun run, FunctionCallContent call, SpendingAssessment assessment);

    /// <summary>
    /// The approval with this ID, if this user proposed it on this thread; <see langword="null"/> otherwise,
    /// which is all a forger, or another user, learns.
    /// </summary>
    PendingApproval? Find(string userId, string threadId, string approvalId);

    /// <summary>Approvals a run proposed and hasn't delivered yet.</summary>
    IReadOnlyList<PendingApproval> ProposedIn(string userId, string threadId, string runId);

    /// <summary>Approvals waiting for this user's answer on the thread.</summary>
    IReadOnlyList<PendingApproval> OpenOn(string userId, string threadId);

    /// <summary>Marks a proposed approval as delivered, answerable until <paramref name="expiresAt"/>.</summary>
    PendingApproval Open(PendingApproval proposed, DateTimeOffset expiresAt);

    /// <summary>Withdraws whatever the run proposed and never delivered.</summary>
    IReadOnlyList<PendingApproval> Withdraw(string userId, string threadId, string runId);

    /// <summary>
    /// Applies every decision, or none if any approval is no longer open for this user. A decision that replays an
    /// earlier one (<see cref="ApprovalDecision.Replaces"/>) applies only while that earlier one is still current.
    /// </summary>
    bool TryDecide(string userId, string threadId, IReadOnlyList<(string ApprovalId, ApprovalDecision Decision)> decisions, out IReadOnlyList<PendingApproval> decided);

    /// <summary>Records that a run has ended, so the answers it carried may be sent again (see <see cref="ApprovalDecision.Repeats"/>).</summary>
    void EndRun(string userId, string threadId, string runId);

    /// <summary>
    /// Spends an approved approval on exactly this call: this user, thread, call, tool and arguments.
    /// Returns <see langword="null"/> if it doesn't match or was already spent.
    /// </summary>
    PendingApproval? TryConsume(string approvalId, string userId, string threadId, string toolCallId, string toolName, JsonElement arguments);

    IReadOnlyList<PendingApproval> All { get; }
}

/// <summary>An in-memory <see cref="IApprovalStore"/> for one server process.</summary>
public sealed class InMemoryApprovalStore(TimeProvider time) : IApprovalStore
{
    private readonly Dictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<PendingApproval> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _approvals.Values.OrderBy(a => a.ProposedAt)];
            }
        }
    }

    public PendingApproval Propose(ApprovalRun run, FunctionCallContent call, SpendingAssessment assessment)
    {
        var approval = new PendingApproval
        {
            // 128 random bits: an ID nobody can derive from a call ID, a run ID or a counter.
            Id = $"apr_{RandomNumberGenerator.GetHexString(32, lowercase: true)}",
            UserId = run.UserId,
            ThreadId = run.ThreadId,
            RunId = run.RunId,
            ClientId = run.ClientId,
            ToolCallId = call.CallId,
            ToolName = call.Name,
            Arguments = ToolArguments.ToJson(call.Arguments),
            Amount = assessment.Amount,
            Summary = assessment.Summary,
            ProposedAt = time.GetUtcNow(),
        };

        lock (_gate)
        {
            _approvals.Add(approval.Id, approval);
        }

        return approval;
    }

    public PendingApproval? Find(string userId, string threadId, string approvalId)
    {
        lock (_gate)
        {
            return _approvals.TryGetValue(approvalId, out var approval) && Owns(approval, userId, threadId) ? approval : null;
        }
    }

    public IReadOnlyList<PendingApproval> ProposedIn(string userId, string threadId, string runId) =>
        Where(a => Owns(a, userId, threadId) && a.RunId == runId && a.Status == ApprovalStatus.Proposed);

    public IReadOnlyList<PendingApproval> OpenOn(string userId, string threadId) =>
        Where(a => Owns(a, userId, threadId) && a.Status == ApprovalStatus.Open);

    public PendingApproval Open(PendingApproval proposed, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            var current = _approvals[proposed.Id];
            if (current.Status != ApprovalStatus.Proposed)
            {
                throw new InvalidOperationException($"Approval {proposed.Id} is {current.Status}, not proposed.");
            }

            return _approvals[proposed.Id] = current with { Status = ApprovalStatus.Open, ExpiresAt = expiresAt };
        }
    }

    public IReadOnlyList<PendingApproval> Withdraw(string userId, string threadId, string runId)
    {
        lock (_gate)
        {
            List<PendingApproval> withdrawn = [];
            foreach (var approval in _approvals.Values.Where(a => Owns(a, userId, threadId) && a.RunId == runId && a.Status == ApprovalStatus.Proposed).ToList())
            {
                withdrawn.Add(_approvals[approval.Id] = approval with { Status = ApprovalStatus.Withdrawn });
            }

            return withdrawn;
        }
    }

    public bool TryDecide(string userId, string threadId, IReadOnlyList<(string ApprovalId, ApprovalDecision Decision)> decisions, out IReadOnlyList<PendingApproval> decided)
    {
        lock (_gate)
        {
            // Check every one first: a request answers all of its approvals or none of them. A replay checks
            // that the decision it takes over is still the current one and that its call hasn't run.
            foreach (var (id, decision) in decisions)
            {
                if (!_approvals.TryGetValue(id, out var current) || !Owns(current, userId, threadId)
                    || (decision.Replaces is null
                        ? current.Status != ApprovalStatus.Open
                        : current.Status == ApprovalStatus.Executed || !ReferenceEquals(current.Decision, decision.Replaces)))
                {
                    decided = [];
                    return false;
                }
            }

            decided = [.. decisions.Select(d => _approvals[d.ApprovalId] = _approvals[d.ApprovalId] with
            {
                Status = d.Decision.Outcome,
                Decision = d.Decision,
            })];
            return true;
        }
    }

    public void EndRun(string userId, string threadId, string runId)
    {
        lock (_gate)
        {
            foreach (var approval in _approvals.Values.Where(a => Owns(a, userId, threadId) && a.Decision is { RunEnded: false } d && d.RunId == runId).ToList())
            {
                _approvals[approval.Id] = approval with { Decision = approval.Decision! with { RunEnded = true } };
            }
        }
    }

    public PendingApproval? TryConsume(string approvalId, string userId, string threadId, string toolCallId, string toolName, JsonElement arguments)
    {
        lock (_gate)
        {
            if (!_approvals.TryGetValue(approvalId, out var approval)
                || !Owns(approval, userId, threadId)
                || approval.Status != ApprovalStatus.Approved
                || approval.ToolCallId != toolCallId
                || approval.ToolName != toolName
                || !ToolArguments.Same(approval.ApprovedArguments, arguments))
            {
                return null;
            }

            return _approvals[approval.Id] = approval with { Status = ApprovalStatus.Executed };
        }
    }

    private static bool Owns(PendingApproval approval, string userId, string threadId) =>
        approval.UserId == userId && approval.ThreadId == threadId;

    private List<PendingApproval> Where(Func<PendingApproval, bool> predicate)
    {
        lock (_gate)
        {
            return [.. _approvals.Values.Where(predicate).OrderBy(a => a.ProposedAt)];
        }
    }
}
