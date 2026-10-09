using AGUI.Client;
using Beacon.Client.Core;
using Ch20.Beacon.Desk.Components;
using Ch20.Beacon.Desk.Components.Surfaces;
using Ch20.DeclarativeUi;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ch20.Beacon.Desk;

// Program lives in a namespace (not top-level statements) so Ch20.Tests can reference the Desk and the
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

        // No header timeout and a bounded connect (Chapter 6); AgentConversation bounds the turn.
        builder.Services.AddAgentHttpClient();

        // Chapter 7's client stack, one conversation per circuit: transport, tap, AGUIChatClient, conversation.
        builder.Services.AddScoped(services => WireEventTap.ForHttp(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(AgentHttp.ClientName), agentEndpoint));
        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));
        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
            }));

        // The surfaces: validated again here, as a consumer, against what this Desk can draw.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(DeskCatalog.CreateValidator());
        builder.Services.AddScoped(services => new SurfaceBoard(services.GetRequiredService<SurfaceValidator>()));

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
