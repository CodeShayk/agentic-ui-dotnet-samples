using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch12.Tests;

/// <summary>
/// The Desk calls the agent as Chapter 6 recommends: no timeout on the wait for headers, which Beacon's agent
/// sends only with the model's first update, and a 10-second connect timeout. AgentConversation bounds the turn.
/// </summary>
public sealed class DeskHttpTests
{
    [Fact]
    public async Task The_agent_client_has_no_header_timeout_and_a_bounded_connect()
    {
        await using var desk = new WebApplicationFactory<global::Program>();

        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent");
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        // The factory wraps the primary handler in its own delegating handlers.
        var handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("BeaconAgent");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(handler).ConnectTimeout);
    }
}
