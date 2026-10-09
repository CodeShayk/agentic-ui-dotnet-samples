// Copied from Chapter 24 (Ch24.Beacon.Agent). Chapter 27 drops StripRawEvent (SecureEventStream always strips it),
// RunTimeout (RunGovernor owns the run budget) and the load-test port.
namespace Ch27.Beacon.Agent.Streaming;

/// <summary>How Beacon streams runs, read from the <c>Beacon:Streaming</c> configuration section.</summary>
public sealed class StreamingOptions
{
    public const string SectionName = "Beacon:Streaming";

    /// <summary>
    /// How long an SSE stream may stay silent before a comment line goes out to keep idle-timeout
    /// proxies and NAT tables from cutting it. <see cref="TimeSpan.Zero"/> turns heartbeats off.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether clients may negotiate the protobuf binding.</summary>
    public bool EnableProtobuf { get; set; } = true;

    /// <summary>How many runs may stream at once; the next one gets 503 before anything runs.</summary>
    public int MaxConcurrentRuns { get; set; } = 500;

    /// <summary>How long a run ID is remembered, so a retried request can't run twice.</summary>
    public TimeSpan RunIdRetention { get; set; } = TimeSpan.FromMinutes(10);
}
