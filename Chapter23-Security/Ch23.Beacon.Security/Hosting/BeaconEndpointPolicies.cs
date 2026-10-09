using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace Ch23.Beacon.Security.Hosting;

/// <summary>
/// The CORS and rate-limit policies both of the chapter's hosts declare on their endpoints, registered once
/// here so the two hosts can't drift apart. The endpoint names the policy; the middleware enforces it.
/// </summary>
public static class BeaconEndpointPolicies
{
    /// <summary>Browsers may call the agent from Beacon Desk's origin only.</summary>
    public const string DeskCors = "BeaconDesk";

    /// <summary>A fixed window of runs per authenticated user.</summary>
    public const string PerUserRuns = "PerUserRuns";

    public static IHostApplicationBuilder AddBeaconEndpointPolicies(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var security = builder.Configuration.GetSection(BeaconSecurityOptions.SectionName).Get<BeaconSecurityOptions>() ?? new();

        // Browsers may call the agent from Beacon Desk's origin only, with the methods and headers it sends.
        builder.Services.AddCors(cors => cors.AddPolicy(DeskCors, policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .WithMethods(HttpMethods.Post, HttpMethods.Get, HttpMethods.Delete)
            .WithHeaders(HeaderNames.ContentType, HeaderNames.Accept, HeaderNames.Authorization)));

        // Each authenticated user gets their own budget of runs; one busy dispatcher can't starve the rest.
        builder.Services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            rateLimiter.AddPolicy(PerUserRuns, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = security.RunsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return builder;
    }
}
