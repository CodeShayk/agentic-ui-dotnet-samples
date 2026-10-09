using AGUI.Client;
using Ch27.Beacon.Desk.Connection;
using Ch27.Beacon.Desk.Components;
using Ch27.Beacon.Desk.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;

namespace Ch27.Beacon.Desk;

public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Telemetry, health checks, service discovery and HTTP resilience, shared with the agent.
        builder.AddServiceDefaults();

        // Sign-in for every page: Microsoft Entra ID when deployed, a development sign-in on a laptop.
        builder.AddDeskAuthentication();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // A stopping replica closes its circuits at once (SignalR does that on shutdown), so the shutdown timeout
        // only has to cover disposing them: each DeskChat cancels its run, which the agent records as canceled.
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(15));

        // "https+http://beacon-agent/agent": service discovery resolves the name, preferring HTTPS.
        // The AppHost (or Azure Container Apps) supplies the address; appsettings.json has a local fallback.
        var agentEndpoint = builder.Configuration["BeaconAgent:Endpoint"]
            ?? throw new InvalidOperationException("BeaconAgent:Endpoint is not configured.");

        // The AG-UI client replaces the standard resilience handler with one that suits a long stream.
        builder.Services.AddHttpClient("BeaconAgent")
            .AddAGUIStreamResilience();
        builder.Services.AddScoped<AgentRefusals>(); // one per circuit, filled by ProblemDetailsHandler (Chapter 14)

        // One AGUIChatClient per circuit (a scope in Blazor Server). Its handlers keep a refusal's reason and put the
        // signed-in user's token on every request; under them, the factory's brings service discovery and the policy.
        builder.Services.AddChatClient(services =>
            {
                var handler = new ProblemDetailsHandler(services.GetRequiredService<AgentRefusals>())
                {
                    InnerHandler = new AgentAccessTokenHandler(
                        services.GetRequiredService<AuthenticationStateProvider>(),
                        services.GetRequiredService<IAgentTokenSource>())
                    {
                        InnerHandler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("BeaconAgent"),
                    },
                };
                var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                return new AGUIChatClient(new AGUIChatClientOptions(http, agentEndpoint));
            },
            ServiceLifetime.Scoped)
            .UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false); // the run's trace, never its content (Chapter 25)

        // The ingress terminates TLS. ASPNETCORE_FORWARDEDHEADERS_ENABLED=true, set in the Container Apps
        // output, makes X-Forwarded-Proto tell the Desk the browser used HTTPS (secure cookies, redirects).

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets().AllowAnonymous();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapSignInAgain();
        app.MapDefaultEndpoints();

        app.Run();
    }
}
