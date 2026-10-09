// Copied from Chapter 14 (Ch14.Beacon.Agent/Approvals). Chapter 18 adds one thing: which subagent invocation asked.
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Approvals;

/// <summary>
/// The approvals the server has asked for. Every change is a check-and-set on the current version, so
/// an approval moves forward once: two requests racing to answer it can't both win, and a tool can't
/// spend it twice.
/// </summary>
public interface IApprovalStore
{
    /// <summary>Records a gated call the model just proposed, under a new server-issued ID.</summary>
    PendingApproval Propose(ApprovalRun run, FunctionCallContent call, SpendingAssessment assessment);

    /// <summary>The approval with this ID on this thread; <see langword="null"/> if there is none, which is all a forger learns.</summary>
    PendingApproval? Find(string threadId, string approvalId);

    /// <summary>Approvals a run proposed and hasn't delivered yet.</summary>
    IReadOnlyList<PendingApproval> ProposedIn(string threadId, string runId);

    /// <summary>Approvals waiting for an answer on the thread.</summary>
    IReadOnlyList<PendingApproval> OpenOn(string threadId);

    /// <summary>Marks a proposed approval as delivered to the client, answerable until <paramref name="expiresAt"/>.</summary>
    PendingApproval Open(PendingApproval proposed, DateTimeOffset expiresAt);

    /// <summary>Withdraws whatever the run proposed and never delivered.</summary>
    IReadOnlyList<PendingApproval> Withdraw(string threadId, string runId);

    /// <summary>Applies every decision, or none if any approval is no longer open.</summary>
    bool TryDecide(string threadId, IReadOnlyList<(string ApprovalId, ApprovalDecision Decision)> decisions, out IReadOnlyList<PendingApproval> decided);

    /// <summary>
    /// Spends the approval for exactly this call: approved in this run, for this tool and these arguments.
    /// Returns <see langword="null"/> if there is none; a second call for the same approval finds none.
    /// </summary>
    PendingApproval? TryConsume(string threadId, string runId, string toolCallId, string toolName, JsonElement arguments);

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
            ThreadId = run.ThreadId,
            RunId = run.RunId,
            ClientId = run.ClientId,
            ToolCallId = call.CallId,
            SubagentRunId = run.SubagentRunId,
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

    public PendingApproval? Find(string threadId, string approvalId)
    {
        lock (_gate)
        {
            return _approvals.TryGetValue(approvalId, out var approval) && approval.ThreadId == threadId ? approval : null;
        }
    }

    public IReadOnlyList<PendingApproval> ProposedIn(string threadId, string runId) =>
        Where(a => a.ThreadId == threadId && a.RunId == runId && a.Status == ApprovalStatus.Proposed);

    public IReadOnlyList<PendingApproval> OpenOn(string threadId) =>
        Where(a => a.ThreadId == threadId && a.Status == ApprovalStatus.Open);

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

    public IReadOnlyList<PendingApproval> Withdraw(string threadId, string runId)
    {
        lock (_gate)
        {
            List<PendingApproval> withdrawn = [];
            foreach (var approval in _approvals.Values.Where(a => a.ThreadId == threadId && a.RunId == runId && a.Status == ApprovalStatus.Proposed).ToList())
            {
                withdrawn.Add(_approvals[approval.Id] = approval with { Status = ApprovalStatus.Withdrawn });
            }

            return withdrawn;
        }
    }

    public bool TryDecide(string threadId, IReadOnlyList<(string ApprovalId, ApprovalDecision Decision)> decisions, out IReadOnlyList<PendingApproval> decided)
    {
        lock (_gate)
        {
            // Check every one first: a request answers all of its approvals or none of them.
            foreach (var (id, _) in decisions)
            {
                if (!_approvals.TryGetValue(id, out var current) || current.ThreadId != threadId || current.Status != ApprovalStatus.Open)
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

    public PendingApproval? TryConsume(string threadId, string runId, string toolCallId, string toolName, JsonElement arguments)
    {
        lock (_gate)
        {
            var approval = _approvals.Values.FirstOrDefault(a =>
                a.ThreadId == threadId
                && a.ToolCallId == toolCallId
                && a.Status == ApprovalStatus.Approved
                && a.Decision?.RunId == runId);

            if (approval is null || approval.ToolName != toolName || !ToolArguments.Same(approval.ApprovedArguments, arguments))
            {
                return null;
            }

            return _approvals[approval.Id] = approval with { Status = ApprovalStatus.Executed };
        }
    }

    private List<PendingApproval> Where(Func<PendingApproval, bool> predicate)
    {
        lock (_gate)
        {
            return [.. _approvals.Values.Where(predicate).OrderBy(a => a.ProposedAt)];
        }
    }
}
