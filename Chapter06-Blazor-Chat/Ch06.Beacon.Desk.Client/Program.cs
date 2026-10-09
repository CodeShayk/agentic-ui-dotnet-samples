using AGUI.Client;
using Ch06.Beacon.Desk.Client;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.AI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// In the browser the chat calls Beacon's agent directly. The agent's CORS policy must allow the
// Desk's origin, and anything that authenticates the call has to live here, in the browser.
var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
    ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");
var streamResponses = !bool.TryParse(builder.Configuration["BeaconAgent:StreamResponses"], out var stream) || stream;

// As on the server, no header timeout: the chat's RunTimeout bounds the run.
builder.Services.AddChatClient(_ => new AGUIChatClient(new AGUIChatClientOptions(
    new HttpClient(new BrowserStreamingHandler(streamResponses) { InnerHandler = new HttpClientHandler() })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    },
    agentEndpoint)));

await builder.Build().RunAsync();
