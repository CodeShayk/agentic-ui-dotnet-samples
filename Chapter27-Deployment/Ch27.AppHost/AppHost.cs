// WithHttpProbe and ProbeType are experimental in Aspire 13.6.
#pragma warning disable ASPIREPROBES001

using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

var builder = DistributedApplication.CreateBuilder(args);

// Where `aspire publish` and `aspire deploy` put the apps. `aspire run` ignores it.
builder.AddAzureContainerAppEnvironment("beacon-env");

var agent = builder.AddProject<Projects.Ch27_Beacon_Agent>("beacon-agent")
    .WithHttpProbe(ProbeType.Readiness, "/health") // the dashboard's health check and Container Apps' probes
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithDeployedModel(builder) // ModelAccess.cs: parameters, managed identity or a secret, publish only
    .WithAgentSecrets(builder)  // Identity.cs: the redaction key and bearer settings, as Container Apps secrets
    .WithVolume("conversations", "/data/conversations", env: "Beacon__Conversations__Directory") // Chapter 22's store
    .WithEnvironment("Beacon__Hosting__DrainSeconds", "45") // inside the 60-second grace period below
    .PublishAsAzureContainerApp((infrastructure, app) =>
    {
        // The conversation store's lease, the approvals and the run ledger live in one process's memory, so the
        // agent runs as one replica until they move to shared storage. It stays warm for the first dispatcher.
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;

        // Give the runs in flight time to finish when the replica is replaced.
        app.Template.TerminationGracePeriodSeconds = 60;
    });

var desk = builder.AddProject<Projects.Ch27_Beacon_Desk>("beacon-desk")
    .WithExternalHttpEndpoints() // the only public endpoint, and every page behind it needs a signed-in user
    .WithHttpProbe(ProbeType.Readiness, "/health")
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithReference(agent)
    .WaitFor(agent)
    .WithDeskSignIn(builder) // Identity.cs: Microsoft Entra ID, publish only
    .PublishAsAzureContainerApp((infrastructure, app) =>
    {
        // Blazor Server keeps each dispatcher's circuit in one replica's memory.
        app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;
        app.Template.Scale.MinReplicas = 1;
    });

// The smoke test runs once the agent is healthy; in Azure it's a job you start after each deployment.
var smoke = builder.AddProject<Projects.Ch27_SmokeTest>("smoke")
    .WithReference(agent)
    .WaitFor(agent)
    .PublishAsAzureContainerAppJob((infrastructure, job) =>
        job.Configuration.TriggerType = ContainerAppJobTriggerType.Manual);

if (builder.ExecutionContext.IsPublishMode)
{
    // In Azure the job signs in as itself (SmokeToken.cs in Ch27.SmokeTest): an identity of its own, given only the
    // agent's dispatcher app role (once, in Entra), not the registry-pull identity the three apps share.
    var smokeIdentity = builder.AddAzureUserAssignedIdentity("smoke-identity");
    smoke.WithAzureUserAssignedIdentity(smokeIdentity)
        .WithEnvironment("Smoke__TokenScope", builder.AddParameter("smoke-token-scope"));
}

// Under `aspire run`, the Desk and the smoke test sign development tokens the agent trusts (Identity.cs).
builder.WithDevelopmentTokens(agent, desk, smoke);

builder.Build().Run();
