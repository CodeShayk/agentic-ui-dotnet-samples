namespace Ch22.Beacon.Agent.Conversations;

/// <summary>One conversation in a list: what the Desk shows before anyone opens it.</summary>
public sealed record ThreadSummary(
    string ThreadId,
    string Title,
    string? WorkOrderId,
    DateTimeOffset UpdatedAt,
    int RunCount,
    int BranchCount,
    bool AwaitingInput,
    bool Compacted);

/// <summary>One run in a thread's tree.</summary>
/// <param name="Outcome"><c>success</c>, <c>interrupt</c>, <c>cancelled</c> or <c>failed</c>, as the run is restated.</param>
/// <param name="Preview">The start of the run's last answer, if it gave one.</param>
public sealed record RunSummary(
    string RunId,
    string? ParentRunId,
    DateTimeOffset StartedAt,
    string Outcome,
    IReadOnlyList<string> InputMessageIds,
    IReadOnlyList<string> OutputMessageIds,
    string? Preview);

/// <summary>A thread's runs as a tree, its default branch and the compaction on that branch.</summary>
/// <param name="Leaves">The runs no other run continues: one per branch. <see cref="HeadRunId"/> is one of them.</param>
public sealed record ThreadDocument(
    string ThreadId,
    string Title,
    string? WorkOrderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? HeadRunId,
    IReadOnlyList<RunSummary> Runs,
    IReadOnlyList<string> Leaves,
    CompactionRecord? Compaction);
