using AGUI.Abstractions;
using Ch23.Beacon.Security.Hosting;
using Ch23.Beacon.Security.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ch23.Beacon.Agent;

public static class HardenedAgentEndpoint
{
    /// <summary>
    /// Maps POST <paramref name="pattern"/> with its controls declared on the endpoint itself. The
    /// middleware in Program.cs enforces them; the endpoint says which policy, origin and limit apply.
    /// </summary>
    public static IEndpointConventionBuilder MapHardenedAgent(
        this IEndpointRouteBuilder endpoints, string pattern, long maxRequestBodyBytes) =>
        endpoints.MapPost(pattern, ([FromBody] RunAgentInput input, HttpContext http, HardenedRunHandler handler) =>
                handler.Handle(input, http))
            .RequireAuthorization(BeaconPolicies.AgentUser)                    // every request, resumes included
            .RequireCors(BeaconEndpointPolicies.DeskCors)                     // browsers: Beacon Desk's origin only
            .RequireRateLimiting(BeaconEndpointPolicies.PerUserRuns)          // a budget per authenticated user
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodyBytes)); // enforced before the JSON is parsed
}
