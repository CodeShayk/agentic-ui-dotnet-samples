namespace Ch08.Beacon.Agent.Faults;

/// <summary>
/// Development-only network faults, so a client's retry rules can be watched against a real server
/// (configuration section <c>Faults</c>). They are ignored outside the Development environment.
/// </summary>
public sealed class FaultOptions
{
    public const string SectionName = "Faults";

    /// <summary>What happens to the first request for each run ID. The second request for it goes through.</summary>
    public FirstAttemptFault FirstAttempt { get; set; } = FirstAttemptFault.None;

    /// <summary>
    /// A tool after which the connection is dropped: the tool runs, its side effect happens and the
    /// client never hears the result. Empty for none.
    /// </summary>
    public string? DropAfterTool { get; set; }
}

/// <summary>How the first request for a run fails.</summary>
public enum FirstAttemptFault
{
    /// <summary>It doesn't.</summary>
    None,

    /// <summary>
    /// The connection is closed before any response, as when a phone loses its signal mid-request. The client
    /// can't tell this from a run that started and lost its first event, so Beacon Field doesn't retry it.
    /// </summary>
    Abort,

    /// <summary>The server answers 503 Service Unavailable with <c>Retry-After: 1</c> before the run starts.</summary>
    Unavailable,
}
