using AGUI.Server;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ch25.Beacon.Agent.Telemetry;

/// <summary>What the telemetry setup decided, for the parts of the app that need to know.</summary>
/// <param name="EnableSensitiveData">Whether message content may appear in spans (Development only).</param>
/// <param name="Exporter">Where telemetry goes: <c>otlp</c>, <c>console</c> or <c>none</c>.</param>
public sealed record BeaconTelemetrySettings(bool EnableSensitiveData, string Exporter);

public static class BeaconTelemetry
{
    /// <summary>The activity source and meter that Microsoft.Extensions.AI's <c>UseOpenTelemetry()</c> uses by default.</summary>
    public const string GenAISourceName = "Experimental.Microsoft.Extensions.AI";

    /// <summary>
    /// Adds OpenTelemetry traces, metrics and logs for Beacon's agent and decides whether message
    /// content may be recorded. Telemetry goes to the OTLP endpoint in <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// (the Aspire dashboard, say) or, when none is configured, to the console.
    /// </summary>
    public static BeaconTelemetrySettings AddBeaconTelemetry(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;

        // Content in telemetry is opt-in, and only on a developer's machine. Setting EnableSensitiveData
        // explicitly also overrides OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT.
        var enableSensitiveData = builder.Environment.IsDevelopment()
            && configuration.GetValue<bool>("Beacon:Telemetry:EnableSensitiveData");

        if (!enableSensitiveData)
        {
            // At Trace, LoggingChatClient logs prompts and responses and FunctionInvokingChatClient logs
            // tool arguments and results. No logging configuration can lift this cap (LogLevelCap.cs).
            builder.Services.CapLogLevel("Microsoft.Extensions.AI", LogLevel.Debug);

            // Exception messages reach spans as status descriptions and exception events.
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new ErrorDetailRedactor()));
        }

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuration["Beacon:Telemetry:ServiceName"] ?? "beacon-agent"))
            .WithTracing(tracing => tracing
                .AddSource(AGUIServerInstrumentation.ActivitySourceName) // agui.run, one per AG-UI run
                .AddSource(GenAISourceName)                              // chat, orchestrate_tools, execute_tool
                .AddAspNetCoreInstrumentation()                          // POST /agent, GET /archive/...
                .AddHttpClientInstrumentation())                         // calls to the model provider and the archive
            .WithMetrics(metrics => metrics
                .AddMeter(GenAISourceName)                               // gen_ai.client.* histograms
                .AddMeter(RunCostMetrics.MeterName)                      // beacon.run.cost
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging();

        string exporter;
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            // All three signals to the Aspire dashboard or any other OTLP receiver.
            otel.UseOtlpExporter();
            exporter = "otlp";
        }
        else if (configuration.GetValue("Beacon:Telemetry:ConsoleExporter", true))
        {
            // No collector configured: print spans and metrics. Logs already go to the console logger.
            otel.WithTracing(tracing => tracing.AddConsoleExporter())
                .WithMetrics(metrics => metrics.AddConsoleExporter());
            exporter = "console";
        }
        else
        {
            exporter = "none";
        }

        builder.Services.AddSingleton<RunCostMetrics>();

        return new BeaconTelemetrySettings(enableSensitiveData, exporter);
    }
}
