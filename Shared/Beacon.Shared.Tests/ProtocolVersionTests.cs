using AGUI.Abstractions;
using Beacon.AGUI.Hosting;
using Beacon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Shared.Tests;

/// <summary>
/// <c>MapAGUIEndpoint</c>'s opt-in <c>ProtocolVersion</c>: off by default, so the stream is exactly what
/// <c>AGUI.Server</c> writes; on, every <c>RUN_STARTED</c> declares the producer's own version.
/// </summary>
public sealed class ProtocolVersionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task By_default_RUN_STARTED_carries_no_protocol_version()
    {
        await using var app = await StartAsync(configure: null);

        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", AGUIWire.UserRun("Hi"), Ct);

        Assert.Equal("RUN_STARTED", frames[0].GetProperty("type").GetString());
        Assert.False(frames[0].TryGetProperty("protocolVersion", out _));
    }

    [Fact]
    public async Task With_ProtocolVersion_set_RUN_STARTED_declares_the_producers_own_version()
    {
        await using var app = await StartAsync(o => o.ProtocolVersion = AGUIProtocol.Version);

        // A newer client declares 1.3; the producer answers with its own version, never an echo.
        var input = AGUIWire.UserRun("Hi");
        input.ProtocolVersion = "1.3";
        var frames = await AGUIWire.RunRawAsync(app.GetTestClient(), "/agent", input, Ct);

        Assert.Equal("1.0", frames[0].GetProperty("protocolVersion").GetString());
        Assert.All(frames.Skip(1), frame => Assert.False(frame.TryGetProperty("protocolVersion", out _)));
        Assert.All(frames, frame => Assert.Empty(AGUIWire.NullProperties(frame)));
    }

    [Fact]
    public async Task A_RUN_STARTED_that_already_declares_a_version_keeps_it()
    {
        BaseEvent[] replay =
        [
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-old", ProtocolVersion = "0.9" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-old" },
            new RunStartedEvent { ThreadId = "t-1", RunId = "r-new" },
            new RunFinishedEvent { ThreadId = "t-1", RunId = "r-new" },
        ];

        List<string?> declared = [];
        await foreach (var evt in replay.ToAsyncEnumerable().WithProtocolVersion(AGUIProtocol.Version, Ct))
        {
            if (evt is RunStartedEvent started)
            {
                declared.Add(started.ProtocolVersion);
            }
        }

        Assert.Equal(["0.9", "1.0"], declared);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("v1.0")]
    [InlineData("1.0.0")]
    [InlineData("")]
    public void A_value_that_isnt_MAJOR_MINOR_is_refused(string version)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIHosting();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapAGUIEndpoint("/agent", o => o.ProtocolVersion = version));
        Assert.Throws<ArgumentException>(() => Array.Empty<BaseEvent>().ToAsyncEnumerable().WithProtocolVersion(version, Ct));
    }

    private static async Task<WebApplication> StartAsync(Action<AGUIEndpointOptions>? configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIHosting();
        builder.Services.AddChatClient(new RecordedChatClient(RecordedChatClient.TextTurn("msg-1", "Hello.")));

        var app = builder.Build();
        app.MapAGUIEndpoint("/agent", configure);
        await app.StartAsync(Ct);
        return app;
    }
}
