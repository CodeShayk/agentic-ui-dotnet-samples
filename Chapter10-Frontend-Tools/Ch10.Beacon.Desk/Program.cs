using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Domain;
using Ch10.Beacon.Desk.Components;
using Ch10.Beacon.Desk.Screen;
using Ch10.Beacon.Desk.Tools;
using Microsoft.Extensions.AI;

namespace Ch10.Beacon.Desk;

// Program lives in a namespace so Ch10.Tests can reference the Desk and the agent side by side.
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

        // The Desk's own view of Contoso Climate's data. Its tools check the agent's arguments against it.
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();

        // One screen, one set of tools and one conversation per circuit.
        builder.Services.AddScoped<DeskScreen>();
        builder.Services.AddScoped<DeskTools>();
        builder.Services.AddScoped(services => WireEventTap.ForHttp(
            services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent"), agentEndpoint));

        // AGUIChatClient already contains a FunctionInvokingChatClient, which runs the Desk's tools. Don't
        // wrap it in another UseFunctionInvocation(): calls reach an outer loop already handled, marked
        // InformationalOnly, so a second loop only adds limits and settings to keep in step with the first.
        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));

        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tools = services.GetRequiredService<DeskTools>().Create(),
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
