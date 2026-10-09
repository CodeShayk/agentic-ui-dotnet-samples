namespace Ch14.Beacon.Desk.Connection;

/// <summary>
/// How the Desk talks HTTP to Beacon's agent. As in Chapter 6, there is no timeout on the wait for the
/// response headers, which the agent sends with the model's first update: a slow model would outlast any
/// fixed value while its run executes, so the conversation's turn timeout is the bound on the run. A dead
/// or unreachable agent still fails fast, because the connect has its own timeout.
/// </summary>
public static class AgentHttp
{
    public const string ClientName = "BeaconAgent";

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The pooled handler the factory manages, with the connect timeout.</summary>
    public static IHttpClientBuilder AddAgentHttp(this IServiceCollection services) =>
        services.AddHttpClient(ClientName)
            .UseSocketsHttpHandler((handler, _) => handler.ConnectTimeout = ConnectTimeout);

    /// <summary>
    /// One client per circuit: the refusal handler, which records into the circuit's <see cref="AgentRefusals"/>,
    /// over the factory's pooled handler.
    /// </summary>
    public static HttpClient CreateClient(IServiceProvider services) =>
        new(new ProblemDetailsHandler(services.GetRequiredService<AgentRefusals>())
        {
            InnerHandler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
