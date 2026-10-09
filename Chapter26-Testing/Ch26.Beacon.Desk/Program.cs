using AGUI.Client;
using Beacon.Client.Core;
using Ch26.Beacon.Desk.Components;
using Microsoft.Extensions.AI;

namespace Ch26.Beacon.Desk;

public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
            ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

        builder.Services.AddHttpClient("BeaconAgent", http =>
            {
                // With a streamed response, HttpClient.Timeout bounds only the wait for the headers, which the agent sends
                // with the model's first update; a slow model outlasts any fixed value. AgentConversation's TurnTimeout is
                // the bound on the run (Chapter 6).
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .UseSocketsHttpHandler((handler, _) =>
                // An unreachable agent still fails fast, and a connect timeout means the run never started.
                handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        // Chapter 7's stack, one per circuit: the SDK's HTTP transport, a tap, AGUIChatClient and the conversation.
        builder.Services.AddScoped(services =>
            WireEventTap.ForHttp(services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent"), agentEndpoint));

        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));

        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
            }));

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
