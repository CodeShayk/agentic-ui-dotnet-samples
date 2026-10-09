using AGUI.Abstractions;

namespace Ch22.Beacon.Agent.Conversations;

/// <summary>
/// One thread as the server holds it: a header and a tree of runs, each run continuing from the end of its
/// parent. A branch is the path from a run back to the start of the thread, and its messages are the
/// conversation as anyone on that branch saw it. The store keeps one of these per thread in memory and
/// writes every change to disk.
/// </summary>
public sealed class StoredConversation
{
    private readonly List<RunRecord> _runs;
    private readonly Lock _gate = new();
    private RunLease? _activeRun;
    private ThreadHeader _header;

    public StoredConversation(ThreadHeader header, IEnumerable<RunRecord> runs)
    {
        _header = header;
        _runs = [.. runs];
    }

    public string ThreadId => _header.ThreadId;

    public ThreadHeader Header
    {
        get
        {
            lock (_gate)
            {
                return _header;
            }
        }
    }

    /// <summary>Every recorded run, oldest first. A copy: safe to read while a run is being recorded.</summary>
    public IReadOnlyList<RunRecord> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public RunRecord? Head => Header.HeadRunId is { } id ? FindRun(id) : null;

    public RunRecord? FindRun(string runId) => Runs.FirstOrDefault(r => r.RunId == runId);

    /// <summary>The runs from the start of the thread to <paramref name="runId"/>, oldest first.</summary>
    public IReadOnlyList<RunRecord> Lineage(string? runId)
    {
        var runs = Runs.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        var lineage = new List<RunRecord>();
        for (var id = runId; id is not null && runs.TryGetValue(id, out var run); id = run.ParentRunId)
        {
            lineage.Add(run);
        }

        lineage.Reverse();
        return lineage;
    }

    /// <summary>The branch that ends with <paramref name="runId"/>: every message, in order, reasoning included.</summary>
    public IReadOnlyList<AGUIMessage> BranchMessages(string? runId) => [.. Lineage(runId).SelectMany(r => r.Messages)];

    /// <summary>Finds where a message was first recorded: the run, and whether the run took it in or produced it.</summary>
    public (RunRecord Run, bool IsInput)? Locate(string messageId)
    {
        foreach (var run in Runs)
        {
            if (run.Input.Any(m => m.Id == messageId))
            {
                return (run, true);
            }

            if (run.Output.Any(m => m.Id == messageId))
            {
                return (run, false);
            }
        }

        return null;
    }

    /// <summary>The server's copy of a message, wherever on the thread it was recorded.</summary>
    public AGUIMessage? FindMessage(string messageId) =>
        Runs.SelectMany(r => r.Messages).FirstOrDefault(m => m.Id == messageId);

    /// <summary>Runs that no other run continues: the tips of the branches.</summary>
    public IReadOnlyList<RunRecord> Leaves
    {
        get
        {
            var runs = Runs;
            var parents = runs.Select(r => r.ParentRunId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return [.. runs.Where(r => !parents.Contains(r.RunId))];
        }
    }

    /// <summary>The compaction that applies to a branch: the one that reaches furthest along it.</summary>
    public CompactionRecord? CompactionFor(IReadOnlyList<AGUIMessage> branch)
    {
        var positions = branch.Select((m, i) => (m.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        return Header.Compactions
            .Where(c => positions.ContainsKey(c.ThroughMessageId))
            .OrderByDescending(c => positions[c.ThroughMessageId])
            .FirstOrDefault();
    }

    /// <summary>
    /// Takes the thread for one run. Runs on a thread are serialized: two at once would both continue the
    /// same branch and interleave their messages. Returns <see langword="null"/> when a run is in progress.
    /// </summary>
    public RunLease? TryBeginRun()
    {
        lock (_gate)
        {
            if (_activeRun is not null)
            {
                return null;
            }

            _activeRun = new RunLease(this);
            return _activeRun;
        }
    }

    internal void Release(RunLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeRun, lease))
            {
                _activeRun = null;
            }
        }
    }

    internal void Apply(ThreadHeader header, RunRecord? recorded = null)
    {
        lock (_gate)
        {
            _header = header;
            if (recorded is not null)
            {
                _runs.Add(recorded);
            }
        }
    }
}

/// <summary>Exclusive use of a thread for one run. Disposing it twice is harmless.</summary>
public sealed class RunLease(StoredConversation conversation) : IDisposable
{
    public StoredConversation Conversation { get; } = conversation;

    public void Dispose() => Conversation.Release(this);
}
