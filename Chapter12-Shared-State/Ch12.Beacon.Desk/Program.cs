using AGUI.Client;
using Beacon.Client.Core;
using Ch12.Beacon.Desk.Components;
using Ch12.Beacon.Desk.Sheet;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
    ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

builder.Services.AddHttpClient("BeaconAgent", http =>
    {
        // No header timeout (Chapter 6): the agent sends its headers with the model's first update, so a slow
        // model would outlast any fixed value while the run executes. AgentConversation bounds the turn.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .UseSocketsHttpHandler((handler, _) =>
        // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
        handler.ConnectTimeout = TimeSpan.FromSeconds(10));

// Chapter 7's stack, one per circuit: transport, tap, AGUIChatClient, conversation.
builder.Services.AddScoped(services => WireEventTap.ForHttp(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent"), agentEndpoint));

builder.Services.AddScoped<IChatClient>(services =>
    new AGUIChatClient(new AGUIChatClientOptions { Transport = services.GetRequiredService<WireEventTap>() }));

builder.Services.AddScoped<DispatcherBoard>();

builder.Services.AddScoped(services =>
{
    var board = services.GetRequiredService<DispatcherBoard>();
    return new AgentConversation(
        services.GetRequiredService<IChatClient>(),
        new ConversationOptions
        {
            Tap = services.GetRequiredService<WireEventTap>(),
            Logger = services.GetRequiredService<ILogger<AgentConversation>>(),

            // The sheet goes out as state on every run (SendState is on by default). The board's
            // selected day goes out as context: read-only facts for the model, on every run too.
            ConfigureInput = (input, _) => input.Context = board.ToContext(),
        });
});

builder.Services.AddScoped<SheetEditor>();

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
