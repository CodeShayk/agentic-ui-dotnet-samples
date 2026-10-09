using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch27.Tests;

public sealed class AppHostTests
{
    [Fact]
    public async Task The_AppHost_wires_the_Desk_to_the_agent_and_exposes_only_the_Desk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ch27_AppHost>(ct);

        var projects = appHost.Resources.OfType<ProjectResource>().ToDictionary(p => p.Name);
        Assert.Equal(["beacon-agent", "beacon-desk", "smoke"], projects.Keys.Order());

        var agent = projects["beacon-agent"];
        var desk = projects["beacon-desk"];

        // The Desk and the smoke test start only when the agent reports healthy.
        Assert.Contains(desk.Annotations.OfType<WaitAnnotation>(), w => w.Resource == agent);
        Assert.Contains(projects["smoke"].Annotations.OfType<WaitAnnotation>(), w => w.Resource == agent);

        // Only the Desk faces the internet; the agent is reachable inside the environment.
        Assert.Contains(desk.Annotations.OfType<EndpointAnnotation>(), e => e.IsExternal);
        Assert.All(agent.Annotations.OfType<EndpointAnnotation>(), e => Assert.False(e.IsExternal));

        // Readiness and liveness probes, which become Container Apps probes when published.
#pragma warning disable ASPIREPROBES001
        Assert.Equal(["/health", "/alive"], agent.Annotations.OfType<EndpointProbeAnnotation>().Select(p => p.Path));
#pragma warning restore ASPIREPROBES001

        // Chapter 22's store lives on a volume, so a new revision keeps the conversations.
        var volume = Assert.Single(agent.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("/data/conversations", volume.Target);
    }

    [Fact]
    public async Task Under_aspire_run_the_agent_trusts_the_key_the_Desk_and_smoke_test_sign_with()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ch27_AppHost>(ct);
        await using var app = await appHost.BuildAsync(ct); // built, not started
        var projects = appHost.Resources.OfType<ProjectResource>().ToDictionary(p => p.Name);

        var agent = await EnvironmentOf(app, projects["beacon-agent"]);
        var desk = await EnvironmentOf(app, projects["beacon-desk"]);
        var smoke = await EnvironmentOf(app, projects["smoke"]);

        // One secret parameter, the same object behind all three variables.
        var key = Assert.IsType<ParameterResource>(agent["Authentication__Schemes__Bearer__SigningKeys__0__Value"]);
        Assert.True(key.Secret);
        Assert.Equal(32, Convert.FromBase64String(await key.GetValueAsync(ct) ?? string.Empty).Length);
        Assert.Same(key, desk["BeaconAgent__DevelopmentTokens__SigningKey"]);
        Assert.Same(key, smoke["BeaconAgent__DevelopmentTokens__SigningKey"]);
        Assert.DoesNotContain("AzureAd__ClientSecret", desk.Keys);
    }

    [Fact]
    public async Task Published_the_agents_redaction_key_and_bearer_settings_come_from_secret_parameters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ch27_AppHost>(
            ["--operation", "publish", "--output-path", Path.Combine(Path.GetTempPath(), "ch27-publish", Guid.NewGuid().ToString("N"))], ct);
        await using var app = await appHost.BuildAsync(ct);
        var projects = appHost.Resources.OfType<ProjectResource>().ToDictionary(p => p.Name);
        var parameters = appHost.Resources.OfType<ParameterResource>().ToDictionary(p => p.Name);

        var agent = await EnvironmentOf(app, projects["beacon-agent"]);
        var desk = await EnvironmentOf(app, projects["beacon-desk"]);

        // Chapter 23's promise: the HMAC key and the Authentication:Schemes:Bearer settings live in the secret store.
        Assert.Same(parameters["redaction-hmac-key"], agent["Beacon__Redaction__HmacKey"]);
        Assert.Same(parameters["agent-token-authority"], agent["Authentication__Schemes__Bearer__Authority"]);
        Assert.Same(parameters["agent-token-audience"], agent["Authentication__Schemes__Bearer__ValidAudiences__0"]);
        Assert.All(["redaction-hmac-key", "agent-token-authority", "agent-token-audience", "desk-client-secret"], name => Assert.True(parameters[name].Secret));

        // The Desk signs in with Microsoft Entra ID; no development key is published.
        Assert.Same(parameters["desk-client-secret"], desk["AzureAd__ClientSecret"]);
        Assert.DoesNotContain("dev-token-key", parameters.Keys);
        Assert.DoesNotContain("BeaconAgent__DevelopmentTokens__SigningKey", desk.Keys);

        // The release gate signs in as itself: the smoke job has an identity of its own and asks for the agent's scope.
        var smoke = await EnvironmentOf(app, projects["smoke"]);
        Assert.Same(parameters["smoke-token-scope"], smoke["Smoke__TokenScope"]);
        Assert.Contains(appHost.Resources.OfType<AzureUserAssignedIdentityResource>(), identity => identity.Name == "smoke-identity");
    }

    [Fact]
    public async Task Published_the_smoke_job_signs_in_with_an_identity_nothing_else_uses()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(Path.GetTempPath(), "ch27-publish", Guid.NewGuid().ToString("N"));

        // `aspire publish` in a test: the publish step writes main.bicep and a module per resource, touching no Azure
        // subscription. --clear-cache keeps it from saving parameter values to the user's deployment state.
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Ch27_AppHost>(
            ["--operation", "publish", "--step", "publish", "--output-path", output, "--clear-cache", "true"], ct);
        await using var app = await appHost.BuildAsync(ct);
        await app.StartAsync(ct);
        var job = await ReadWhenWrittenAsync(Path.Combine(output, "smoke", "smoke.bicep"), ct);
        var agent = await ReadWhenWrittenAsync(Path.Combine(output, "beacon-agent", "beacon-agent.bicep"), ct);
        var desk = await ReadWhenWrittenAsync(Path.Combine(output, "beacon-desk", "beacon-desk.bicep"), ct);

        // The job runs as smoke-identity, and AZURE_CLIENT_ID tells SmokeToken which of its identities that is.
        Assert.Contains("'${smoke_identity_outputs_id}': { }", job, StringComparison.Ordinal);
        Assert.Matches(@"name: 'AZURE_CLIENT_ID'\s+value: smoke_identity_outputs_clientid", job);
        Assert.Matches(@"name: 'AZURE_TOKEN_CREDENTIALS'\s+value: 'ManagedIdentityCredential'", job);
        Assert.Contains("Microsoft.ManagedIdentity/userAssignedIdentities",
            await ReadWhenWrittenAsync(Path.Combine(output, "smoke-identity", "smoke-identity.bicep"), ct), StringComparison.Ordinal);

        // Neither app can mint a dispatcher token with it.
        Assert.DoesNotContain("smoke_identity", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("smoke_identity", desk, StringComparison.Ordinal);
    }

    // The publish step runs in the background once the app starts.
    private static async Task<string> ReadWhenWrittenAsync(string path, CancellationToken ct)
    {
        for (var i = 0; i < 300 && !File.Exists(path); i++)
        {
            await Task.Delay(100, ct);
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    // What a resource's environment callbacks put in its environment, as objects (parameters stay parameters).
    private static async Task<Dictionary<string, object>> EnvironmentOf(DistributedApplication app, IResource resource)
    {
        var context = new EnvironmentCallbackContext(app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), resource, []);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return context.EnvironmentVariables.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
