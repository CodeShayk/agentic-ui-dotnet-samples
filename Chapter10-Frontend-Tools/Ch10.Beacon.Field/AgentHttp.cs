namespace Ch10.Beacon.Field;

/// <summary>
/// The <see cref="HttpClient"/> Beacon Field calls its agent with, set up as Chapter 6 recommends. It depends
/// on nothing from .NET MAUI, so Ch10.Tests compiles this file in as a link and checks it without the workload.
/// </summary>
public static class AgentHttp
{
    /// <summary>How long to wait for a connection to the agent. A connect timeout means the run never started.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the client. The app keeps one for its lifetime.</summary>
    public static HttpClient Create() => new(CreateHandler())
    {
        // No header timeout: the agent sends its headers with the model's first update, so a slow model would
        // outlast any fixed value while the run executes. The turn has its own bound (TurnTimeout).
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Creates the handler, whose connect timeout still fails an unreachable agent fast.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectTimeout = ConnectTimeout };
}
