using AGUI.Client;
using Beacon.Client.Core;
using Ch22.Beacon.Desk.Components;
using Ch22.Beacon.Desk.Sessions;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.Desk;

// Program lives in a namespace so Ch22.Tests can reference the Desk and the agent side by side.
public sealed class Program
{
    /// <summary>The named HttpClient that carries AG-UI runs.</summary>
    public const string AgentClient = "BeaconAgent";

    /// <summary>The named HttpClient for the agent's thread API.</summary>
    public const string ThreadsClient = "BeaconThreads";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
            ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

        builder.Services.AddHttpClient(AgentClient, http =>
            {
                // With a streamed response, HttpClient.Timeout bounds only the wait for the headers, and the agent
                // sends them with the model's first update. A slow model would outlast any fixed value while the run
                // executes, so AgentConversation's TurnTimeout is the only bound on the run (Chapter 6).
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .UseSocketsHttpHandler((handler, _) =>
                // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
                handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        // The thread API answers from the store, never from a model, so a fixed bound suits it.
        builder.Services.AddHttpClient(ThreadsClient, http => http.Timeout = TimeSpan.FromSeconds(30))
            .UseSocketsHttpHandler((handler, _) => handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        builder.Services.AddSingleton(TimeProvider.System);

        // Chapter 7's stack, per circuit: the SDK's HTTP transport, a tap, AGUIChatClient and the conversation.
        builder.Services.AddScoped(services => WireEventTap.ForHttp(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(AgentClient), agentEndpoint));

        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));

        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
                TimeProvider = services.GetRequiredService<TimeProvider>(),
            }));

        // The agent's thread API sits beside its AG-UI endpoint: /agent/threads.
        builder.Services.AddScoped(services => new ThreadClient(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(ThreadsClient), $"{agentEndpoint.TrimEnd('/')}/threads"));
        builder.Services.AddScoped<ConversationSessions>();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }
}
