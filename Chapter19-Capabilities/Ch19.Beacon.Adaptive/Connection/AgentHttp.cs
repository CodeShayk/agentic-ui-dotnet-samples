namespace Ch19.Beacon.Adaptive.Connection;

/// <summary>
/// How both clients reach the agent, as Chapter 6 sets it up: no header timeout and a bounded connect.
/// The run's own bound is <c>ConversationOptions.TurnTimeout</c>, and discovery has its own.
/// </summary>
public static class AgentHttp
{
    /// <summary>How long to wait for a TCP connection to the agent.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A handler whose connect fails fast when the agent is down or unreachable.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectTimeout = ConnectTimeout };

    /// <summary>A client for Beacon Field, which keeps one for the app's lifetime.</summary>
    public static HttpClient CreateClient() => new(CreateHandler())
    {
        // With a streamed response, Timeout bounds only the wait for the headers, and the agent sends them
        // with the model's first update, so a slow model would outlast any fixed value.
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
