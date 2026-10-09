namespace Ch08.Field.Core.Connection;

/// <summary>
/// The connection broke after a run had started: a truncated run. It is an <see cref="IOException"/>, so
/// <c>AgentConversation</c> reports it as <c>FailureKind.StreamEnded</c> however the network phrased the
/// break, and it carries the run and the last event that arrived. It is never retried.
/// </summary>
public sealed class TruncatedRunException : IOException
{
    /// <summary>Creates the exception for a run that broke after <paramref name="eventsReceived"/> events.</summary>
    /// <param name="runId">The run that broke.</param>
    /// <param name="eventsReceived">How many of its events arrived before the break.</param>
    /// <param name="lastEventType">The type of the last event that arrived.</param>
    /// <param name="innerException">What the network reported.</param>
    public TruncatedRunException(string runId, int eventsReceived, string lastEventType, Exception innerException)
        : base(
            $"The connection dropped after run {runId} started ({eventsReceived} events, last {lastEventType}). The run may have finished on the server; it isn't retried.",
            innerException)
    {
        RunId = runId;
        EventsReceived = eventsReceived;
        LastEventType = lastEventType;
    }

    /// <summary>The run that broke.</summary>
    public string RunId { get; }

    /// <summary>How many of its events arrived before the break.</summary>
    public int EventsReceived { get; }

    /// <summary>The type of the last event that arrived, such as <c>TOOL_CALL_END</c>.</summary>
    public string LastEventType { get; }
}
