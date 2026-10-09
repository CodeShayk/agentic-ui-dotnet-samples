namespace Ch17.Field.Capture;

/// <summary>
/// The <see cref="HttpClient"/> Beacon Field calls its agent with, set up as Chapter 6 recommends. It lives
/// here, outside the .NET MAUI app, so Ch17.Tests checks it without the workload.
/// </summary>
public static class AgentHttp
{
    /// <summary>How long to wait for a connection to the agent. A connect timeout means the run never started.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the client. The app keeps one for its lifetime.</summary>
    public static HttpClient Create() => new(CreateHandler())
    {
        // No header timeout: the agent sends its headers with the model's first update, so a slow model would
        // outlast any fixed value while the run executes. A request may also carry 20 MB of attachments over a
        // mobile connection. The turn's own bound (TurnTimeout) covers both the upload and the run.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Creates the handler, whose connect timeout still fails an unreachable agent fast.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { ConnectTimeout = ConnectTimeout };
}
