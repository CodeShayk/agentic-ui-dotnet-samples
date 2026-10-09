using Ch19.Beacon.Adaptive.Connection;
using Ch19.Beacon.Desk.Session;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch19.Tests;

/// <summary>Both clients reach the agent as Chapter 6 sets it up: no header timeout, a bounded connect.</summary>
public sealed class AgentHttpTests
{
    [Fact]
    public async Task The_Desk_bounds_the_connect_and_leaves_the_run_to_the_conversation()
    {
        await using var desk = new WebApplicationFactory<global::Program>();

        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient(DeskSession.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        HttpMessageHandler handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(DeskSession.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<SocketsHttpHandler>(handler).ConnectTimeout);
    }

    [Fact]
    public void Beacon_Fields_client_bounds_the_connect_and_leaves_the_run_to_the_conversation()
    {
        // MauiProgram registers AgentHttp.CreateClient(), which wraps AgentHttp.CreateHandler().
        using var http = AgentHttp.CreateClient();
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        using var handler = AgentHttp.CreateHandler();
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }
}
