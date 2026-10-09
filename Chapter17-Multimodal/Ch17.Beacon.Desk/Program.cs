using AGUI.Client;
using Beacon.Client.Core;
using Ch17.Beacon.Desk.Components;
using Ch17.Media;
using Microsoft.Extensions.AI;

namespace Ch17.Beacon.Desk;

// Program lives in a namespace so Ch17.Tests can reference the Desk and the agent side by side.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
            ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

        // No header timeout (Chapter 6): the agent sends its headers with the model's first update, and a request
        // can carry 20 MB of attachments, so the upload and the run are bounded by the conversation's turn timeout.
        // A dead or unreachable agent still fails fast on the connect timeout.
        builder.Services.AddHttpClient("BeaconAgent", http => http.Timeout = Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) => handler.ConnectTimeout = TimeSpan.FromSeconds(10));

        // The same rules the agent enforces, checked here before anything is uploaded.
        builder.Services.AddSingleton(MediaPolicy.Beacon);

        // One conversation per circuit, as in Chapter 7.
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
