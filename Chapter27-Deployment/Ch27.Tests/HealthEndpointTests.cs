using System.Net;
using Beacon.Testing;
using Ch27.Beacon.Agent.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ch27.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task The_agent_answers_its_liveness_and_readiness_probes_without_a_token()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(token: null);
        var ct = TestContext.Current.CancellationToken;

        using var alive = await http.GetAsync("/alive", ct);
        using var ready = await http.GetAsync("/health", ct);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("Healthy", await alive.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_revision_with_a_missing_model_setting_is_alive_but_never_ready()
    {
        await using var agent = AgentHost.With(("AI:Provider", "AzureOpenAI"), ("AI:Model", "gpt-5-mini"));
        var http = agent.CreateClient(token: null);
        var ct = TestContext.Current.CancellationToken;

        using var alive = await http.GetAsync("/alive", ct);
        using var ready = await http.GetAsync("/health", ct);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // The probe's answer is one word. Which setting is missing stays in the server's health report.
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_draining_replica_fails_readiness_and_refuses_new_runs_with_retry_after()
    {
        await using var agent = new AgentHost();
        var http = agent.CreateClient(DevTokens.Dana);
        var ct = TestContext.Current.CancellationToken;

        agent.Services.GetRequiredService<RunGovernor>().BeginDrain(); // what SIGTERM does

        using var alive = await http.GetAsync("/alive", ct);
        using var ready = await http.GetAsync("/health", ct);
        using var run = await http.PostAsJsonRunAsync(AGUIWire.UserRun("What is the status of WO-1001?"), ct);

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, run.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), run.Headers.RetryAfter?.Delta);
    }
}
