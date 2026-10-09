using AGUI.Client;
using Ch06.Beacon.Desk.Components;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// With Interactive Server rendering the chat runs here, so the call to Beacon's agent is server to
// server: no CORS, and credentials for the agent never reach the browser (a backend for frontend).
var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
    ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

builder.Services.AddHttpClient("BeaconAgent", http =>
    {
        // With a streamed response, HttpClient.Timeout bounds only the wait for the headers, and the agent
        // sends them with the model's first update. A slow model would outlast any fixed value while the
        // run executes, so the chat's own bound (DeskChat.RunTimeout) is the only one on the run.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .UseSocketsHttpHandler((handler, _) =>
        // A dead or unreachable agent still fails fast. A connect timeout means the run never started.
        handler.ConnectTimeout = TimeSpan.FromSeconds(10));

// One AGUIChatClient per circuit (a scope in Blazor Server), over a factory-managed HttpClient.
builder.Services.AddChatClient(
    services => new AGUIChatClient(new AGUIChatClientOptions(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent"),
        agentEndpoint)),
    ServiceLifetime.Scoped);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Ch06.Beacon.Desk.Client._Imports).Assembly);

app.Run();
