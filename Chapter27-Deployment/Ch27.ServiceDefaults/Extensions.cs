// Adapted from Aspire's service-defaults project template (aspire-servicedefaults 13.6.0), MIT License.
using AGUI.Client;
using AGUI.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// The defaults every Beacon service shares: OpenTelemetry, health checks, service discovery and
/// HTTP resilience. Each service calls <see cref="AddServiceDefaults{TBuilder}"/> once and
/// <see cref="MapDefaultEndpoints"/> after it builds the app.
/// </summary>
public static class Extensions
{
    /// <summary>Readiness: every registered check must pass before the replica gets traffic.</summary>
    public const string HealthEndpointPath = "/health";

    /// <summary>Liveness: only checks tagged <c>live</c>. A failure here gets the replica restarted.</summary>
    public const string AlivenessEndpointPath = "/alive";

    /// <summary>The tag that puts a check on the liveness endpoint.</summary>
    public const string LiveTag = "live";

    // The source and meter name UseOpenTelemetry() gives Microsoft.Extensions.AI's spans and metrics (Chapter 25).
    private const string GenAISourceName = "Experimental.Microsoft.Extensions.AI";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Right for short request/response calls. An AG-UI client opts out with AddAGUIStreamResilience().
            http.AddStandardResilienceHandler();

            // "http://beacon-agent" resolves to wherever the AppHost (or Container Apps) put the agent.
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(GenAISourceName))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(AGUIServerInstrumentation.ActivitySourceName) // agui.run on the agent
                .AddSource(AGUIClientInstrumentation.ActivitySourceName) // frontend tools on a client
                .AddSource(GenAISourceName)                              // chat, orchestrate_tools, execute_tool
                .AddAspNetCoreInstrumentation(aspNetCore =>
                    // Probes arrive every few seconds; tracing them would bury the runs.
                    aspNetCore.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation());

        // The AppHost sets OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard; in Azure it points at the
        // environment's collector or dashboard. With no endpoint nothing is exported.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // The process answers. Nothing more: a liveness check that calls a dependency restarts
            // healthy replicas whenever that dependency has a bad minute.
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> and <c>/alive</c> in every environment, because the container platform's
    /// probes need them in production. The default writer returns one word (Healthy, Degraded or
    /// Unhealthy) and no check details, and no check here calls the model. The platform's probes carry no
    /// credentials, so both stay anonymous in an app that requires a signed-in user everywhere else.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();

        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        }).AllowAnonymous();

        return app;
    }
}
