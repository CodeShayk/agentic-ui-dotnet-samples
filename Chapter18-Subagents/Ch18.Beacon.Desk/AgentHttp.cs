namespace Ch18.Beacon.Desk;

/// <summary>The named <see cref="HttpClient"/> the Desk talks to its agent with, set up as in Chapter 6.</summary>
public static class AgentHttp
{
    /// <summary>The client's name in <see cref="IHttpClientFactory"/>.</summary>
    public const string ClientName = "BeaconAgent";

    /// <summary>How long to wait for a TCP connection to the agent.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registers the client: no header timeout, a bounded connect.</summary>
    /// <param name="services">The Desk's services.</param>
    public static IHttpClientBuilder AddAgentHttpClient(this IServiceCollection services) =>
        services.AddHttpClient(ClientName, http =>
            {
                // With a streamed response, HttpClient.Timeout bounds only the wait for the headers, and the agent
                // sends them with the model's first update, so a slow model would outlast any fixed value.
                // AgentConversation's TurnTimeout is the bound on the run.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
            .UseSocketsHttpHandler((handler, _) => handler.ConnectTimeout = ConnectTimeout);
}
