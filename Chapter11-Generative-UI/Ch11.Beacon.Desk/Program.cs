using AGUI.Client;
using Beacon.Client.Core;
using Ch11.Beacon.ComponentViews;
using Ch11.Beacon.Desk.Components;
using Ch11.Beacon.Desk.Components.Generative;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Desk;

// Program lives in a namespace (not top-level statements) so Ch11.Tests can reference the Desk and the
// agent together.
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
                // No header timeout (Chapter 6): the agent sends its headers with the model's first update,
                // so a slow model would outlast any fixed value while the run executes. AgentConversation
                // bounds the turn.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .UseSocketsHttpHandler((handler, _) =>
                // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
                handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        // Chapter 7's client stack, one conversation per circuit: transport, tap, AGUIChatClient, conversation.
        // The tap is what lets a component render while its arguments are still streaming.
        builder.Services.AddScoped(services => WireEventTap.ForHttp(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent"), agentEndpoint));
        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
            }));

        // The components this Desk can render, and a projector per conversation that turns tool calls into them.
        builder.Services.AddSingleton(ComponentRegistry.CreateDefault());
        builder.Services.AddScoped(services => services.GetRequiredService<ComponentRegistry>().CreateProjector());

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
