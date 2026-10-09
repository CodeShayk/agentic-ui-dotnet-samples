using Beacon.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ch27.Beacon.Agent.Hosting;

/// <summary>Not ready while draining, so the platform stops sending this replica new runs.</summary>
public sealed class DrainHealthCheck(RunGovernor governor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(governor.IsDraining
            ? HealthCheckResult.Unhealthy($"Draining, {governor.ActiveRuns} run(s) in flight.")
            : HealthCheckResult.Healthy());
}

/// <summary>
/// Not ready when the model settings can't work: a revision deployed with a missing endpoint or
/// deployment name never takes traffic. It reads configuration only. It never calls the model,
/// because a probe every few seconds would cost tokens and tie the replica's health to the provider's.
/// </summary>
public sealed class ModelConfigurationHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ai = AIClientFactory.ReadOptions(configuration);
        string? missing = ai.Provider switch
        {
            AIProvider.AzureOpenAI when string.IsNullOrWhiteSpace(ai.Endpoint) => "AI:Endpoint",
            AIProvider.AzureOpenAI when string.IsNullOrWhiteSpace(ai.Model) => "AI:Model",
            AIProvider.OpenAI when string.IsNullOrWhiteSpace(ai.ApiKey) => "AI:ApiKey",
            _ => null,
        };

        return Task.FromResult(missing is null
            ? HealthCheckResult.Healthy($"Provider {ai.Provider}.")
            : HealthCheckResult.Unhealthy($"Provider {ai.Provider} needs {missing}."));
    }
}
