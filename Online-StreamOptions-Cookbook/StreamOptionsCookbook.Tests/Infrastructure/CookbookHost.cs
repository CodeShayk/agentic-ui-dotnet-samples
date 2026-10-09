using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using StreamOptionsCookbook;
using Beacon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace StreamOptionsCookbook.Tests.Infrastructure;

/// <summary>Hosts one recipe behind the book's <c>MapAGUIEndpoint</c>, in memory, with a deterministic model.</summary>
internal sealed class CookbookHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CookbookHost(WebApplication app) => _app = app;

    public static async Task<CookbookHost> StartAsync(
        IChatClient chatClient,
        Func<AGUIStreamOptions, AGUIStreamOptions>? recipe = null,
        bool registerContentTypes = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddAGUIHosting();
        if (registerContentTypes)
        {
            // Content the pipeline invents must be serializable by the endpoint (MapContent, MapInterrupt).
            builder.Services.Configure<JsonOptions>(options => CookbookJson.RegisterContentTypes(options.SerializerOptions));
        }

        builder.Services.AddChatClient(chatClient);

        var app = builder.Build();

        // The book's glue builds the stream options per request: a fresh AGUIStreamOptions for every run.
        app.MapAGUIEndpoint("/agent", endpoint =>
            endpoint.StreamOptions = _ => recipe?.Invoke(new AGUIStreamOptions()) ?? new AGUIStreamOptions());

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new CookbookHost(app);
    }

    /// <summary>Posts a run and returns each SSE <c>data:</c> payload as the server wrote it.</summary>
    public Task<IReadOnlyList<JsonElement>> RunAsync(RunAgentInput input) =>
        AGUIWire.RunRawAsync(_app.GetTestClient(), "/agent", input, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
