using System.Net;
using System.Net.Sockets;
using AGUI.Abstractions;
using Beacon.Testing;
using Microsoft.Extensions.Logging;

namespace Ch05.Tests;

/// <summary>What the client sees and what the server keeps when the model provider fails.</summary>
public sealed class FailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HostKind.Plain)]
    [InlineData(HostKind.AgentFramework)]
    public async Task An_unreachable_provider_is_a_sanitized_RUN_ERROR_and_a_logged_exception(HostKind kind)
    {
        await using var host = AgentHosts.Start(kind, AgentHosts.Ollama(ClosedPort()));

        var events = await AGUIWire.RunAsync(
            host.CreateClient(), "/agent", AGUIWire.UserRun("What is the status of WO-1001?"), Ct);

        // The run failed before it began: a lone RUN_ERROR, which the spec allows as a stream's first event.
        var error = Assert.IsType<RunErrorEvent>(Assert.Single(events));
        Assert.Equal("StreamingError", error.Code);
        Assert.DoesNotContain("127.0.0.1", error.Message);

        // The details stay on the server, where UseLogging recorded the provider's exception.
        var logged = Assert.Single(host.Logs, e =>
            e.Category == "Microsoft.Extensions.AI.LoggingChatClient" && e.Level == LogLevel.Error);
        Assert.IsType<HttpRequestException>(logged.Exception);
    }

    private static Uri ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }
}
