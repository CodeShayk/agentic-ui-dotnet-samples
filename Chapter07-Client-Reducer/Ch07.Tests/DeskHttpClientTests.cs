using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using DeskProgram = Program;

namespace Ch07.Tests;

/// <summary>
/// The Desk keeps Chapter 6's timeout rule: no header timeout, because the agent sends its headers only
/// with the model's first update, and a connect timeout, so a dead agent still fails fast and provably
/// before the run started.
/// </summary>
public sealed class DeskHttpClientTests
{
    [Fact]
    public async Task The_desk_waits_for_a_slow_model_but_fails_a_dead_connect_fast()
    {
        await using var desk = new WebApplicationFactory<DeskProgram>();

        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent");
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        var handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("BeaconAgent");
        Assert.Equal(TimeSpan.FromSeconds(10), Primary(handler).ConnectTimeout);
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler handler) => handler switch
    {
        SocketsHttpHandler sockets => sockets,
        DelegatingHandler { InnerHandler: { } inner } => Primary(inner),
        _ => throw new InvalidOperationException($"Expected a SocketsHttpHandler at the bottom, found {handler.GetType().Name}."),
    };
}
