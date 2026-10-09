using AGUI.Client;
using Beacon.Client.Core;
using Ch16.Beacon.Desk.Components;
using Ch16.Beacon.Desk.Reasoning;
using Microsoft.Extensions.AI;

namespace Ch16.Beacon.Desk;

// Program lives in a namespace so Ch16.Tests can reference the Desk and the agent side by side.
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
                // With a streamed response, HttpClient.Timeout bounds only the wait for the headers, and the agent
                // sends them with the model's first update, so a slow model would outlast any fixed value while
                // its run executes. AgentConversation's turn timeout is the bound on the run (Chapter 6).
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .UseSocketsHttpHandler((handler, _) =>
                // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
                handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        builder.Services.AddSingleton(TimeProvider.System);

        // Chapter 7's stack: the SDK's HTTP transport, a tap, AGUIChatClient and the conversation, per circuit.
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
                TimeProvider = services.GetRequiredService<TimeProvider>(),

                // Every run returns the sealed reasoning the conversation holds, unmodified.
                ConfigureInput = ReasoningRoundTrip.ConfigureInput,
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
