using System.Net;
using System.Runtime.CompilerServices;
using Beacon.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch27.Tests;

/// <summary>
/// A model whose first update takes a while: a reasoning model thinking, or a local model loading.
/// The AG-UI converter writes RUN_STARTED, and with it the response headers, only when the first
/// update arrives, so the client waits this long for headers.
/// </summary>
internal sealed class SlowStartChatClient(IChatClient inner, TimeSpan firstUpdateDelay) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(firstUpdateDelay, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }
}

/// <summary>
/// An AG-UI endpoint on Kestrel (real sockets, so the client's own handlers run) in front of the
/// scripted model, counting the runs it was asked to start. It can refuse the first runs with 503
/// and Retry-After, as a draining replica does, or with another status and Retry-After.
/// </summary>
internal sealed class SlowAgent : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _runsStarted;
    private int _refusalsLeft;
    private int _refusals;

    private SlowAgent(WebApplication app, int refusals)
    {
        _app = app;
        _refusalsLeft = refusals;
    }

    /// <summary>How many runs it refused.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    public Uri Address { get; private set; } = default!;

    public int RunsStarted => Volatile.Read(ref _runsStarted);

    public static async Task<SlowAgent> StartAsync(
        TimeSpan firstUpdateDelay, CancellationToken cancellationToken, int refuseFirst = 0,
        HttpStatusCode refusal = HttpStatusCode.ServiceUnavailable, string retryAfter = "1")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAGUIHosting();
        builder.Services.AddChatClient(new SlowStartChatClient(
            new ScriptedChatClient(BeaconScripts.Default, new ScriptedChatClientOptions { ChunkDelay = TimeSpan.FromMilliseconds(20) }),
            firstUpdateDelay));

        var agent = new SlowAgent(builder.Build(), refuseFirst);
        agent._app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (Interlocked.Decrement(ref agent._refusalsLeft) >= 0)
                {
                    Interlocked.Increment(ref agent._refusals);
                    context.Response.StatusCode = (int)refusal;
                    context.Response.Headers.RetryAfter = retryAfter;
                    return;
                }

                Interlocked.Increment(ref agent._runsStarted);
            }

            await next(context);
        });
        agent._app.MapAGUIEndpoint("/agent");

        await agent._app.StartAsync(cancellationToken);
        var address = agent._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        agent.Address = new Uri(address + "/");
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
