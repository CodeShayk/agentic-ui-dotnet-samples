// Copied from Chapter 23 (Ch23.Beacon.Security). Chapter 27 adds Chapter 24's concurrency limit and gives each limit
// its own status: one RateLimiterOptions serves both, so the 429 belongs to the per-user policy, not to the options.
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Ch27.Beacon.Agent.Streaming;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Ch27.Beacon.Agent.Security.Hosting;

/// <summary>
/// The CORS and rate-limit policies the agent declares on its endpoints. The endpoint names the policy; the
/// middleware enforces it.
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
        var streaming = builder.Configuration.GetSection(StreamingOptions.SectionName).Get<StreamingOptions>() ?? new();

        // Browsers may call the agent from Beacon Desk's origin only, with the methods and headers it sends.
        builder.Services.AddCors(cors => cors.AddPolicy(DeskCors, policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .WithMethods(HttpMethods.Post, HttpMethods.Get, HttpMethods.Delete)
            .WithHeaders(HeaderNames.ContentType, HeaderNames.Accept, HeaderNames.Authorization)));

        builder.Services.AddRateLimiter(rateLimiter =>
        {
            // Chapter 24: the whole replica takes MaxConcurrentRuns at once. Beyond that, a run gets 503 and
            // Retry-After before anything runs, which any client may retry. This is the options-wide default.
            rateLimiter.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            rateLimiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.CompletedTask;
            };
            rateLimiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                http.GetEndpoint()?.Metadata.GetMetadata<ConcurrentRunsLimit>() is not null
                    ? RateLimitPartition.GetConcurrencyLimiter("runs", _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = streaming.MaxConcurrentRuns,
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter(string.Empty));

            // Chapter 23: each user gets their own budget of runs, so one busy dispatcher can't starve the rest.
            // The policy carries its own OnRejected, which the middleware uses instead of the default above.
            rateLimiter.AddPolicy(PerUserRuns, new PerUserRunsPolicy(security.RunsPerMinute));
        });

        return builder;
    }

    /// <summary>Puts an endpoint under the replica-wide limit on concurrent runs.</summary>
    public static TBuilder LimitConcurrentRuns<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ConcurrentRunsLimit());

    /// <summary>Endpoint metadata: this endpoint's requests count against the concurrent-run limit.</summary>
    public sealed class ConcurrentRunsLimit;

    /// <summary>A fixed window per authenticated user, answered with 429 and the window's Retry-After.</summary>
    private sealed class PerUserRunsPolicy(int runsPerMinute) : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            return ValueTask.CompletedTask;
        };

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = runsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }
}
