namespace Ch08.Field.Core.Connection;

/// <summary>What happened to the connection behind one run, as <see cref="ResilientAgentTransport"/> saw it.</summary>
/// <param name="RunId">The run's ID. A retry sends the same input, so every attempt carries the same ID.</param>
/// <param name="Attempt">Which attempt this is about, starting at 1.</param>
/// <param name="Stage">What happened.</param>
public sealed record ConnectionEvent(string RunId, int Attempt, ConnectionStage Stage)
{
    /// <summary>The failure, for <see cref="ConnectionStage.Retrying"/>, <see cref="ConnectionStage.GaveUp"/> and a dropped <see cref="ConnectionStage.Truncated"/>.</summary>
    public Exception? Error { get; init; }

    /// <summary>How long the transport waits before the next attempt, for <see cref="ConnectionStage.Retrying"/>.</summary>
    public TimeSpan? RetryDelay { get; init; }

    /// <summary>How many events had arrived when this happened.</summary>
    public int EventsReceived { get; init; }

    /// <summary>The policy's attempt limit, so a UI can say "attempt 2 of 3".</summary>
    public int MaxAttempts { get; init; }
}

/// <summary>The stages of a run's connection.</summary>
public enum ConnectionStage
{
    /// <summary>An attempt failed before the first event in a way worth retrying; another attempt follows.</summary>
    Retrying,

    /// <summary>No attempt got the run started; the failure goes to the caller.</summary>
    GaveUp,

    /// <summary>The first event arrived. From here on nothing is retried.</summary>
    Started,

    /// <summary>The stream ended after a terminal event (<c>RUN_FINISHED</c> or <c>RUN_ERROR</c>).</summary>
    Finished,

    /// <summary>The stream broke or ended after the run started but before a terminal event: a truncated run.</summary>
    Truncated,

    /// <summary>The caller cancelled the run (Stop, or the app went to the background).</summary>
    Cancelled,
}
