using Ch18.Beacon.Agent.Approvals;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Agent.Coordination;

/// <summary>
/// What the coordinator needs to know about the run it is in: the thread, run and client its approvals
/// belong to, and the approvals the dispatcher answered in this run's resume entries (already checked by
/// the resume gate). The handler puts it on the run's <see cref="ChatOptions"/>.
/// </summary>
/// <param name="Approval">The thread, run and client, as Chapter 14's approval code expects them.</param>
/// <param name="Decided">The approvals this run's resume entries decided; empty for a new question.</param>
public sealed record CoordinatorRun(ApprovalRun Approval, IReadOnlyList<PendingApproval> Decided)
{
    /// <summary>The key on <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string Key = "beacon.coordinator_run";

    public string ThreadId => Approval.ThreadId;

    public static CoordinatorRun? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out CoordinatorRun? run) is true ? run : null;
}

/// <summary>
/// A specialist invocation that stopped to wait for a dispatcher, kept on the server until the answer comes.
/// The continuation is built from this record, never from the client's history, which the client could edit.
/// </summary>
/// <param name="ThreadId">The thread it belongs to.</param>
/// <param name="SubagentRunId">The invocation's ID; the continuation reuses it.</param>
/// <param name="Specialist">Which specialist it was.</param>
/// <param name="DelegationCallId">The coordinator's internal delegation call.</param>
/// <param name="Messages">The specialist's conversation up to the request, without the request itself.</param>
public sealed record SuspendedInvocation(
    string ThreadId,
    string SubagentRunId,
    string Specialist,
    string DelegationCallId,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>Where suspended invocations wait. Each one is taken once, by the run that continues it.</summary>
public interface ISuspensionStore
{
    void Save(SuspendedInvocation invocation);

    /// <summary>Removes and returns the invocation; <see langword="null"/> if there is none (or it was already continued).</summary>
    SuspendedInvocation? Take(string threadId, string subagentRunId);
}

/// <summary>An in-memory <see cref="ISuspensionStore"/>. A real one expires entries with their approvals.</summary>
public sealed class InMemorySuspensionStore : ISuspensionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), SuspendedInvocation> _waiting = new();

    public void Save(SuspendedInvocation invocation) => _waiting[(invocation.ThreadId, invocation.SubagentRunId)] = invocation;

    public SuspendedInvocation? Take(string threadId, string subagentRunId) =>
        _waiting.TryRemove((threadId, subagentRunId), out var invocation) ? invocation : null;
}
