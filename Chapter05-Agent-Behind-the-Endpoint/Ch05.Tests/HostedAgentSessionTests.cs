using System.Net;
using Beacon.AI;
using Beacon.Testing;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch05.Tests;

/// <summary>
/// The DI-registered route to sessions: AddAIAgent(...).WithInMemorySessionStore() and the
/// IHostedAgentBuilder overload of MapAGUIServer. Its default asks for caller isolation.
/// </summary>
public sealed class HostedAgentSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithInMemorySessionStore_refuses_runs_until_an_isolation_key_provider_is_registered()
    {
        await using var app = await StartAsync(beacon => beacon.WithInMemorySessionStore());

        using var response = await PostAsync(app);

        // Strict isolation by default: no AgentIsolationKeyProvider, no session, no run.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("isolation key is required", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithInMemorySessionStore_without_isolation_runs_but_shares_sessions_by_thread_id()
    {
        await using var app = await StartAsync(beacon => beacon.WithInMemorySessionStore(withIsolation: false));

        using var response = await PostAsync(app);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(Action<IHostedAgentBuilder> configureSessions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.EnvironmentName = "Development"; // a 500 page instead of a thrown exception
        builder.Services.AddAGUIServer();
        builder.Services.AddChatClient(new ScriptedChatClient(options: new() { ChunkDelay = TimeSpan.Zero }));

        var beacon = builder.AddAIAgent("beacon", (sp, name) =>
            sp.GetRequiredService<IChatClient>().AsAIAgent(instructions: BeaconInstructions.Default, name: name));
        configureSessions(beacon);

        var app = builder.Build();
        app.MapAGUIServer(beacon, "/agent");
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent")
        {
            Content = new StringContent(
                """{"threadId":"thread-1","runId":"run-1","messages":[{"id":"user-1","role":"user","content":"Hello"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await app.GetTestClient().SendAsync(request, Ct);
    }
}
