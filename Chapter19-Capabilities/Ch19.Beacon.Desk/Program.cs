using Ch19.Beacon.Adaptive.Connection;
using Ch19.Beacon.Desk.Components;
using Ch19.Beacon.Desk.Session;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var agentEndpoint = new Uri(builder.Configuration["BeaconAgent:Endpoint"]
    ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured."));

// No header timeout and a bounded connect (Chapter 6); AgentConversation bounds the turn.
builder.Services.AddHttpClient(DeskSession.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(AgentHttp.CreateHandler);

// One session per circuit: discovery, the plan and a conversation built to match it.
builder.Services.AddScoped(services => new DeskSession(
    services.GetRequiredService<IHttpClientFactory>().CreateClient(DeskSession.HttpClientName),
    agentEndpoint,
    services.GetRequiredService<ILoggerFactory>()));

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
