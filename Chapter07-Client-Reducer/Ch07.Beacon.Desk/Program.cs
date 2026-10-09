using AGUI.Client;
using Beacon.Client.Core;
using Ch07.Beacon.Desk.Components;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
    ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

builder.Services.AddHttpClient("BeaconAgent", http =>
    {
        // HttpClient.Timeout bounds only the wait for headers, which the agent sends with the model's
        // first update (Chapter 6); AgentConversation's TurnTimeout bounds the turn.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .UseSocketsHttpHandler((handler, _) => handler.ConnectTimeout = TimeSpan.FromSeconds(10));

// One conversation per circuit. The tap sits on the SDK's HTTP transport, AGUIChatClient on the tap,
// and the conversation on both: updates from the chat client, early events from the tap.
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
