// Copied from Chapter 23 (Ch23.Beacon.Agent). Chapter 27 adds Chapter 24's concurrency limit and its own drain.
using AGUI.Abstractions;
using Ch27.Beacon.Agent.Hosting;
using Ch27.Beacon.Agent.Security.Hosting;
using Ch27.Beacon.Agent.Security.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ch27.Beacon.Agent.Runs;

public static class HardenedAgentEndpoint
{
    /// <summary>
    /// Maps POST <paramref name="pattern"/> with its controls declared on the endpoint itself. The
    /// middleware in Program.cs enforces them; the endpoint says which policy, origin and limit apply.
    /// </summary>
    public static IEndpointConventionBuilder MapHardenedAgent(
        this IEndpointRouteBuilder endpoints, string pattern, long maxRequestBodyBytes, RunGovernor governor) =>
        endpoints.MapPost(pattern, ([FromBody] RunAgentInput input, HttpContext http, HardenedRunHandler handler) =>
                handler.HandleAsync(input, http))
            .RequireAuthorization(BeaconPolicies.AgentUser)                     // every request, resumes included
            .RequireCors(BeaconEndpointPolicies.DeskCors)                      // browsers: Beacon Desk's origin only
            .RequireRateLimiting(BeaconEndpointPolicies.PerUserRuns)           // a budget per user: 429
            .LimitConcurrentRuns()                                             // a budget per replica: 503
            .WithMetadata(new RequestSizeLimitAttribute(maxRequestBodyBytes))  // enforced before the JSON is parsed
            .DisableRequestTimeout()                                           // runs have a budget instead
            .AddEndpointFilter(async (context, next) =>
            {
                if (governor.IsDraining)
                {
                    // Nothing has run yet, so the client may safely send this run to another replica.
                    context.HttpContext.Response.Headers.RetryAfter = "1";
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Beacon is restarting",
                        detail: "This replica is shutting down and didn't start the run. Send it again.");
                }

                return await next(context);
            });
}
