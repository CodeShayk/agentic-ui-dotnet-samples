// Copied from Chapter 23 (Ch23.Beacon.Security).
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Ch27.Beacon.Agent.Security.Identity;

/// <summary>Beacon's authorization policies.</summary>
public static class BeaconPolicies
{
    /// <summary>Anyone who may talk to the agent at all: a dispatcher or a technician.</summary>
    public const string AgentUser = "BeaconAgentUser";

    public static IServiceCollection AddBeaconAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AgentUser, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(BeaconClaims.DispatcherRole, BeaconClaims.TechnicianRole)
                // A technician's token must say which technician it is; tools scope their data by it.
                .RequireAssertion(context =>
                    !context.User.IsInRole(BeaconClaims.TechnicianRole)
                    || context.User.HasClaim(c => c.Type == BeaconClaims.TechnicianId && c.Value.Length > 0)));

        return services;
    }
}
