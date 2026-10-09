using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ch27.Tests;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public void AddServiceDefaults_registers_a_liveness_check_that_depends_on_nothing()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        var self = Assert.Single(registrations);
        Assert.Equal("self", self.Name);
        Assert.Contains(Extensions.LiveTag, self.Tags);
    }

    [Fact]
    public void The_agent_adds_readiness_checks_that_stay_off_the_liveness_endpoint()
    {
        using var agent = new AgentHost();
        var registrations = agent.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Equal(["self", "drain", "model-configuration"], registrations.Select(r => r.Name));

        // A drain or a bad model setting takes the replica out of rotation. It must not get it restarted.
        Assert.All(registrations.Where(r => r.Name != "self"), r => Assert.DoesNotContain(Extensions.LiveTag, r.Tags));
    }
}
