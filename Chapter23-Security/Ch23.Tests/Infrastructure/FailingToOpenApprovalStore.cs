using System.Text.Json;
using Ch23.Beacon.Security.Approvals;
using Microsoft.Extensions.AI;

namespace Ch23.Tests.Infrastructure;

/// <summary>An approval store whose write fails as a run ends, to break a stream under the server.</summary>
public sealed class FailingToOpenApprovalStore(IApprovalStore inner) : IApprovalStore
{
    public IReadOnlyList<PendingApproval> All => inner.All;

    public PendingApproval Propose(ApprovalRun run, FunctionCallContent call, SpendingAssessment assessment) => inner.Propose(run, call, assessment);

    public PendingApproval? Find(string userId, string threadId, string approvalId) => inner.Find(userId, threadId, approvalId);

    public IReadOnlyList<PendingApproval> ProposedIn(string userId, string threadId, string runId) => inner.ProposedIn(userId, threadId, runId);

    public IReadOnlyList<PendingApproval> OpenOn(string userId, string threadId) => inner.OpenOn(userId, threadId);

    public PendingApproval Open(PendingApproval proposed, DateTimeOffset expiresAt) =>
        throw new IOException("The approval store is unavailable.");

    public IReadOnlyList<PendingApproval> Withdraw(string userId, string threadId, string runId) => inner.Withdraw(userId, threadId, runId);

    public bool TryDecide(string userId, string threadId, IReadOnlyList<(string ApprovalId, ApprovalDecision Decision)> decisions, out IReadOnlyList<PendingApproval> decided) =>
        inner.TryDecide(userId, threadId, decisions, out decided);

    public void EndRun(string userId, string threadId, string runId) => inner.EndRun(userId, threadId, runId);

    public PendingApproval? TryConsume(string approvalId, string userId, string threadId, string toolCallId, string toolName, JsonElement arguments) =>
        inner.TryConsume(approvalId, userId, threadId, toolCallId, toolName, arguments);
}
