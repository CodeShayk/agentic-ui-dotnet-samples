using System.Net;
using Beacon.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch05.Tests;

/// <summary>
/// MapAGUIServer returns an ordinary endpoint convention builder, so ASP.NET Core's endpoint conventions
/// (authorization, CORS, rate limiting, request timeouts) attach to it. What it doesn't offer is a place for
/// code of your own between reading the RunAgentInput and converting it.
/// </summary>
public sealed class EndpointConventionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rate_limit_attaches_to_the_MapAGUIServer_endpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAGUIServer();
        builder.Services.AddRateLimiter(limits => limits.AddFixedWindowLimiter("one-run", window =>
        {
            window.PermitLimit = 1;
            window.Window = TimeSpan.FromMinutes(1);
        }));

        await using var app = builder.Build();
        app.UseRateLimiter();

        var agent = new ScriptedChatClient(options: new() { ChunkDelay = TimeSpan.Zero })
            .AsAIAgent(instructions: BeaconInstructions.Default, name: "beacon");
        app.MapAGUIServer("/agent", agent).RequireRateLimiting("one-run");
        await app.StartAsync(Ct);

        using var first = await PostAsync(app);
        using var second = await PostAsync(app);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode); // the limiter's default rejection
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
        var response = await app.GetTestClient().SendAsync(request, Ct);
        await response.Content.ReadAsStringAsync(Ct); // let the run finish
        return response;
    }
}
