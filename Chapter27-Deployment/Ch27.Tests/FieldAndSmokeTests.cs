using System.Text.Json;
using Ch27.Beacon.Field.Core;
using Ch27.SmokeTest;
using Microsoft.Extensions.Configuration;

namespace Ch27.Tests;

public sealed class FieldSettingsTests
{
    [Fact]
    public void A_default_build_points_Beacon_Field_at_the_local_agent()
    {
        var settings = FieldSettings.Load();

        Assert.Equal(new Uri("http://localhost:5270/agent"), settings.Endpoint);
    }

    [Theory]
    [InlineData("Staging", "https://beacon-staging.contoso-climate.example/agent")]
    [InlineData("Production", "https://beacon.contoso-climate.example/agent")]
    public void Every_deployed_environment_uses_HTTPS_a_custom_domain_and_the_agents_scope(string environment, string endpoint)
    {
        // The files the build chooses between with -p:BeaconEnvironment, read as the embedded copy would be.
        var file = Path.Combine(AppContext.BaseDirectory, "FieldSettings", $"appsettings.{environment}.json");
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var agent = json.RootElement.GetProperty("BeaconAgent");

        Assert.Equal(endpoint, agent.GetProperty("Endpoint").GetString());
        Assert.DoesNotContain("azurecontainerapps.io", endpoint, StringComparison.Ordinal); // survives a rebuilt environment
        Assert.Equal(["api://beacon-agent/access_as_user"], agent.GetProperty("Scopes").EnumerateArray().Select(s => s.GetString()));
    }
}

/// <summary>The production smoke test, against the combined agent on a real socket.</summary>
public sealed class SmokeTests
{
    [Fact]
    public async Task The_smoke_test_passes_against_the_secured_agent_and_proves_it_refuses_anonymous_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = AgentHost.StartOnKestrel(settings: ("AI:ScriptedChunkDelayMilliseconds", "25"));
        using var http = new HttpClient { BaseAddress = agent.Address };

        var token = SmokeToken.CreateDevelopmentToken(Convert.FromBase64String(DevTokens.Key), DateTimeOffset.UtcNow);
        var report = await StreamSmokeTest.RunAsync(http, token, ct);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Checks));
        Assert.Contains(report.Checks, c => c.Name == "POST /agent, no token" && c.Passed);
    }

    [Fact]
    public async Task In_Azure_the_smoke_test_signs_in_only_as_its_own_identity()
    {
        // Smoke:TokenScope without AZURE_CLIENT_ID: the job was published without smoke-identity. Rather than try
        // whichever identity the host offers first (the registry-pull identity the apps share), it stops.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Smoke:TokenScope"] = "api://beacon-agent/.default" })
            .Build();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SmokeToken.GetAsync(configuration, TestContext.Current.CancellationToken));

        Assert.Contains("AZURE_CLIENT_ID", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_token_the_smoke_test_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = AgentHost.StartOnKestrel();
        using var http = new HttpClient { BaseAddress = agent.Address };

        var report = await StreamSmokeTest.RunAsync(http, token: null, ct);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Name == "POST /agent" && !c.Passed);
    }
}
