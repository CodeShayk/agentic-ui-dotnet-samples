using System.Security.Cryptography;
using AGUI.Client;
using Beacon.Client.Core;
using Ch14.Beacon.Desk.Components;
using Ch14.Beacon.Desk.Components.Approvals;
using Ch14.Beacon.Desk.Connection;
using Microsoft.Extensions.AI;

namespace Ch14.Beacon.Desk;

public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
            ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

        // No header timeout, a 10-second connect timeout (Chapter 6); AgentConversation bounds the turn.
        builder.Services.AddAgentHttp();

        // The agent's latest refusal on this circuit, with its reason, which the SDK's transport would discard.
        builder.Services.AddScoped(_ => new AgentRefusals());

        builder.Services.AddSingleton(TimeProvider.System);

        // The dispatcher's time zone, read from her browser once per circuit (deadlines show in her time).
        builder.Services.AddScoped(_ => new DispatcherTime());

        // Each circuit (a browser tab) labels its requests, so the agent's audit can say which Desk decided.
        builder.Services.AddScoped(_ => new DeskClient($"desk-{RandomNumberGenerator.GetHexString(8, lowercase: true)}"));

        // Chapter 7's stack: the SDK's HTTP transport, a tap, AGUIChatClient and the conversation.
        builder.Services.AddScoped(services =>
        {
            var http = AgentHttp.CreateClient(services);
            http.DefaultRequestHeaders.Add(DeskClient.HeaderName, services.GetRequiredService<DeskClient>().Id);
            return WireEventTap.ForHttp(http, agentEndpoint);
        });

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
