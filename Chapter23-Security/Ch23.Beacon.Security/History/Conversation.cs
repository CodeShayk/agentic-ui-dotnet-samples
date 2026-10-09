using AGUI.Abstractions;

namespace Ch23.Beacon.Security.History;

/// <summary>How a run ended, as the server saw it (Chapter 22's rule for restating it).</summary>
public enum RunEnding
{
    /// <summary>The run ended with <c>RUN_FINISHED</c>.</summary>
    Finished,

    /// <summary>The run ended with <c>RUN_ERROR</c>; the conversation keeps its message and code.</summary>
    Failed,

    /// <summary>The client left before a terminal event, so the server stopped the run: a stop, restated as cancelled.</summary>
    Cancelled,

    /// <summary>The run broke under the server before a terminal event: restated as <c>RUN_ERROR</c>.</summary>
    Broken,
}

/// <summary>
/// One user's conversation on one thread, as the server recorded it: the messages it accepted from
/// the user and the messages its own runs produced, plus the frontend calls it is waiting on. This is
/// the history the model sees; the copy a client sends back is used only to find what's new. Open
/// approvals live in the approval store, keyed the same way.
/// </summary>
public sealed class Conversation(string userId, string threadId)
{
    private readonly List<AGUIMessage> _messages = [];
    private readonly Dictionary<string, AGUIMessage> _byId = new(StringComparer.Ordinal);
    private RunLease? _activeRun;
    private readonly Lock _gate = new();

    public string UserId { get; } = userId;

    public string ThreadId { get; } = threadId;

    /// <summary>A copy of the transcript: the thread API may read it while a run appends.</summary>
    public IReadOnlyList<AGUIMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>The last run recorded on the thread. The transcript is linear, so a run continues this one.</summary>
    public string? LastRunId { get; private set; }

    /// <summary>How the last run ended, so the thread API restates it truthfully.</summary>
    public RunEnding LastRunEnding { get; private set; }

    /// <summary>The <c>RUN_ERROR</c> message and code of the last run, when it ended <see cref="RunEnding.Failed"/>.</summary>
    public (string Message, string? Code)? LastRunError { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Marks a run as recorded, with how it ended (Chapter 22 keeps a whole run record; this checkpoint keeps
    /// the latest run's ID and ending).
    /// </summary>
    public void Recorded(string runId, DateTimeOffset at, RunEnding ending, (string Message, string? Code)? error = null)
    {
        LastRunId = runId;
        LastRunEnding = ending;
        LastRunError = ending == RunEnding.Failed ? error : null;
        UpdatedAt = at;
    }

    /// <summary>Frontend tool calls the last run left for the client, by call ID, with the tool's name.</summary>
    public Dictionary<string, string> PendingClientCalls { get; } = new(StringComparer.Ordinal);

    public bool TryGetMessage(string id, out AGUIMessage message)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out message!);
        }
    }

    /// <summary>Whether the transcript holds a result for this tool call: the model has read it.</summary>
    public bool HasResultFor(string toolCallId)
    {
        lock (_gate)
        {
            return _messages.OfType<AGUIToolMessage>().Any(m => m.ToolCallId == toolCallId);
        }
    }

    public void Append(IEnumerable<AGUIMessage> messages)
    {
        lock (_gate)
        {
            foreach (var message in messages)
            {
                if (_byId.TryAdd(message.Id, message))
                {
                    _messages.Add(message);
                }
            }
        }
    }

    /// <summary>
    /// Takes the conversation for one run. Runs on a thread are serialized: two at once would race
    /// to answer the same approval and interleave their messages. Returns <see langword="null"/> when
    /// another run holds it.
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
}

/// <summary>Exclusive use of a conversation for one run. Disposing it twice is harmless.</summary>
public sealed class RunLease(Conversation conversation) : IDisposable
{
    public Conversation Conversation { get; } = conversation;

    public void Dispose() => Conversation.Release(this);
}
