using System.Security.Cryptography;
using AGUI.Client;
using Beacon.Client.Core;
using Ch18.Beacon.Desk.Activity;
using Ch18.Beacon.Desk.Components;
using Microsoft.Extensions.AI;

namespace Ch18.Beacon.Desk;

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

        builder.Services.AddSingleton(TimeProvider.System);

        // Each circuit (a browser tab) labels its requests, so the agent's approval audit can say which Desk decided.
        builder.Services.AddScoped(_ => new DeskClient($"desk-{RandomNumberGenerator.GetHexString(8, lowercase: true)}"));

        // Chapter 7's stack: the SDK's HTTP transport, a tap, AGUIChatClient and the conversation.
        builder.Services.AddScoped(services =>
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(AgentHttp.ClientName);
            http.DefaultRequestHeaders.Add(DeskClient.HeaderName, services.GetRequiredService<DeskClient>().Id);
            return WireEventTap.ForHttp(http, agentEndpoint);
        });

        // Chapter 13's ResultAlignedTransport between the tap and the chat client: three specialists call tools
        // at once, and without it everything after the first TOOL_CALL_END would wait for every result.
        builder.Services.AddScoped<IChatClient>(services =>
            new AGUIChatClient(new AGUIChatClientOptions { Transport = new ResultAlignedTransport(services.GetRequiredService<WireEventTap>()) }));

        builder.Services.AddScoped(services => new AgentConversation(
            services.GetRequiredService<IChatClient>(),
            new ConversationOptions
            {
                Tap = services.GetRequiredService<WireEventTap>(),
                Logger = services.GetRequiredService<ILogger<AgentConversation>>(),
                TimeProvider = services.GetRequiredService<TimeProvider>(),
            }));

        // The keyboard path from the chat to an approval waiting on the board, one per circuit.
        builder.Services.AddScoped<ApprovalFocus>();

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

/// <summary>This Desk tab's label for itself, sent as <c>X-Beacon-Client</c> on every run (Chapter 14).</summary>
/// <param name="Id">The label.</param>
public sealed record DeskClient(string Id)
{
    public const string HeaderName = "X-Beacon-Client";
}
