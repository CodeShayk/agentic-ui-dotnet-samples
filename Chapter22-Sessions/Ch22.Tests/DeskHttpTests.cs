using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using DeskProgram = Ch22.Beacon.Desk.Program;

namespace Ch22.Tests;

/// <summary>
/// Beacon Desk's HTTP clients follow Chapter 6: no HttpClient.Timeout on a run, whose headers wait for the model,
/// but a connect timeout, so an unreachable agent still fails fast. The thread API, served from the store, keeps a bound.
/// </summary>
public sealed class DeskHttpTests
{
    [Fact]
    public void The_run_client_never_times_out_a_slow_model_but_fails_fast_on_connect()
    {
        using var desk = new WebApplicationFactory<DeskProgram>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        var clients = desk.Services.GetRequiredService<IHttpClientFactory>();

        using var runs = clients.CreateClient(DeskProgram.AgentClient);
        Assert.Equal(Timeout.InfiniteTimeSpan, runs.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), PrimaryHandler(desk, DeskProgram.AgentClient).ConnectTimeout);

        using var threads = clients.CreateClient(DeskProgram.ThreadsClient);
        Assert.Equal(TimeSpan.FromSeconds(30), threads.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), PrimaryHandler(desk, DeskProgram.ThreadsClient).ConnectTimeout);
    }

    private static SocketsHttpHandler PrimaryHandler(WebApplicationFactory<DeskProgram> desk, string name)
    {
        HttpMessageHandler handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }
}
