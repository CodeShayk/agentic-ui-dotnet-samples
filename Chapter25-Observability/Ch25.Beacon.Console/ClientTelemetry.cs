using System.Diagnostics;
using AGUI.Client;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ch25.Beacon.Console;

/// <summary>Tracing for the console client: its spans join the agent's in one trace.</summary>
public static class ClientTelemetry
{
    /// <summary>The console's own source: one <c>beacon.console.ask</c> span per question.</summary>
    public static readonly ActivitySource Source = new("Ch25.Beacon.Console");

    /// <summary>
    /// Starts a tracer provider. Spans go to <paramref name="otlpEndpoint"/> (OTLP over gRPC, the Aspire
    /// dashboard's port 4317, say) when one is given, and to the console when <paramref name="showSpans"/> is true.
    /// </summary>
    public static TracerProvider Start(string? otlpEndpoint, bool showSpans)
    {
        var tracing = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(resource => resource.AddService("beacon-console"))
            .AddSource(Source.Name)                                  // beacon.console.ask
            .AddSource("Experimental.Microsoft.Extensions.AI")       // the client-side chat span
            .AddSource(AGUIClientInstrumentation.ActivitySourceName) // the client's tool loop and frontend tools
            .AddHttpClientInstrumentation();                         // the POST that carries traceparent

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
        }

        if (showSpans)
        {
            tracing.AddConsoleExporter();
        }

        return tracing.Build();
    }

    /// <summary>
    /// Adds a client-side chat span around each run. Content stays out of it: set explicitly, <c>EnableSensitiveData</c>
    /// no longer follows <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>, the agent's rule too (section 25.3).
    /// </summary>
    public static IChatClient Instrument(IChatClient client) =>
        client.AsBuilder().UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = false).Build();
}
