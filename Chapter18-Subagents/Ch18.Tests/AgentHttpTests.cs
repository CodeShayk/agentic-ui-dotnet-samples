using Ch18.Beacon.Desk;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch18.Tests;

/// <summary>The Desk's client for the agent: Chapter 6's pattern, read from the Desk's own host.</summary>
public sealed class AgentHttpTests
{
    [Fact]
    public async Task The_Desk_bounds_the_connect_and_leaves_the_run_to_the_conversation()
    {
        await using var desk = new WebApplicationFactory<Program>();

        // No header timeout: a slow model's first update would trip any fixed value.
        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AgentHttp.ClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        // A bounded connect, so an unreachable agent still fails fast.
        HttpMessageHandler handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(AgentHttp.ClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(handler).ConnectTimeout);
    }
}
