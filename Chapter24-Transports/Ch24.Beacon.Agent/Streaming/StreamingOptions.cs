namespace Ch24.Beacon.Agent.Streaming;

/// <summary>How Beacon streams runs, read from the <c>Beacon:Streaming</c> configuration section.</summary>
public sealed class StreamingOptions
{
    public const string SectionName = "Beacon:Streaming";

    /// <summary>
    /// How long an SSE stream may stay silent before a comment line goes out to keep idle-timeout
    /// proxies and NAT tables from cutting it. <see cref="TimeSpan.Zero"/> turns heartbeats off.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether to drop <c>rawEvent</c>, the SDK's serialized copy of the <c>ChatResponseUpdate</c>
    /// each event came from. Clients ignore it; it repeats provider details and dominates the bytes.
    /// </summary>
    public bool StripRawEvent { get; set; } = true;

    /// <summary>Whether clients may negotiate the protobuf binding.</summary>
    public bool EnableProtobuf { get; set; } = true;

    /// <summary>How many runs may stream at once; the next one gets 503 before anything runs.</summary>
    public int MaxConcurrentRuns { get; set; } = 500;

    /// <summary>How long one run may take, every model call and tool included.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a run ID is remembered, so a retried request can't run twice.</summary>
    public TimeSpan RunIdRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>An extra cleartext port that speaks only HTTP/2, for load tests; none by default.</summary>
    public int? Http2CleartextPort { get; set; }
}
