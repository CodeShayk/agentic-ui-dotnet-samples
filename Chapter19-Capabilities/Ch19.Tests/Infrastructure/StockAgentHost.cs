using Beacon.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch19.Tests.Infrastructure;

/// <summary>
/// An agent on the book's stock hosting glue (Chapter 4's <c>MapAGUIEndpoint</c>) with nothing from this
/// chapter: no capabilities document and no <c>protocolVersion</c> on <c>RUN_STARTED</c>. It plays the
/// agent that says nothing about itself, and the peer that looks older than 1.0.
/// </summary>
public sealed class StockAgentHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StockAgentHost(WebApplication app) => _app = app;

    public HttpClient CreateClient() => _app.GetTestClient();

    public Uri Endpoint => new(_app.GetTestServer().BaseAddress, "agent");

    public static async Task<StockAgentHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIHosting();
        builder.Services.AddChatClient(new ScriptedChatClient(options: new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero }));
        var app = builder.Build();
        app.MapAGUIEndpoint("/agent");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new StockAgentHost(app);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
