using System.Net;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch24.Tests;

/// <summary>
/// The agent on Kestrel behind Ch24.Proxy (YARP) with a 1.5-second idle timeout, and a model that stays
/// silent for 3 seconds before each reply: the situation heartbeats exist for. Every margin is wide (a
/// heartbeat six times as often as the timeout, a silence twice as long), so a busy machine can't flip a result.
/// </summary>
public sealed class ProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Heartbeats_carry_a_silent_run_through_an_idle_timeout()
    {
        await using var agent = StartAgent(heartbeat: "00:00:00.250");
        await using var proxy = new ProxyHost(AddressOf(agent), activityTimeout: "00:00:01.500");
        using var http = proxy.CreateClient();

        using var response = await PostAsync(http, "/direct/agent");
        var events = await AgentHost.ReadEventsAsync(response, Ct);

        Assert.IsType<RunFinishedEvent>(events[^1]);
    }

    [Fact]
    public async Task Without_heartbeats_the_proxy_cuts_the_run_after_RUN_STARTED()
    {
        await using var agent = StartAgent(heartbeat: "00:00:00");
        await using var proxy = new ProxyHost(AddressOf(agent), activityTimeout: "00:00:01.500");
        using var http = proxy.CreateClient();

        using var response = await PostAsync(http, "/direct/agent");
        List<BaseEvent> received = [];
        var cut = await Record.ExceptionAsync(async () =>
        {
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            await foreach (var evt in new AGUI.Formatting.SseEventStreamFormatter().ReadAsync(body, Ct))
            {
                received.Add(evt);
            }
        });

        // A truncated run: the stream ends without a terminal event, with or without an error.
        Assert.IsType<RunStartedEvent>(Assert.Single(received));
        Assert.True(cut is null or IOException or HttpRequestException, $"Unexpected: {cut}");
    }

    [Fact]
    public async Task Chapter_4s_endpoint_gets_a_504_because_its_headers_wait_for_the_model()
    {
        await using var agent = StartAgent(heartbeat: "00:00:00.250");
        await using var proxy = new ProxyHost(AddressOf(agent), activityTimeout: "00:00:01.500");
        using var http = proxy.CreateClient();

        using var response = await PostAsync(http, "/direct/agent/baseline");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    private static AgentHost StartAgent(string heartbeat)
    {
        var agent = new AgentHost(
            ("Beacon:Streaming:HeartbeatInterval", heartbeat),
            ("Beacon:Simulation:FirstChunkDelay", "00:00:03"));
        agent.UseKestrel(0);
        agent.StartServer();
        return agent;
    }

    private static string AddressOf(AgentHost agent) =>
        agent.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                $$"""{"threadId":"thread-24","runId":"run-{{Guid.NewGuid():N}}","messages":[{"id":"user-1","role":"user","content":"What is the status of WO-1001?"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd(AgentHost.SseType);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    /// <summary>Ch24.Proxy as its <c>Program.Main</c> builds it, pointed at the test's agent.</summary>
    private sealed class ProxyHost(string agentAddress, string activityTimeout) : WebApplicationFactory<global::Ch24.Proxy.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ReverseProxy:Clusters:agent:Destinations:beacon:Address", agentAddress);
            builder.UseSetting("ReverseProxy:Clusters:agent:HttpRequest:ActivityTimeout", activityTimeout);
        }
    }
}
