// Adapted from Chapter 25 (Ch25.Beacon.Agent's BeaconTelemetry). The OpenTelemetry setup itself moved to
// Ch27.ServiceDefaults; what stays here is the decision about content, which must be registered first.
using OpenTelemetry.Trace;

namespace Ch27.Beacon.Agent.Telemetry;

public static class ContentGate
{
    /// <summary>
    /// Decides whether message content may reach telemetry (only on a developer's machine, and only when asked)
    /// and, when it may not, caps <c>Microsoft.Extensions.AI</c>'s logs below <c>Trace</c> and strips exception
    /// messages from spans. Call it before <c>AddServiceDefaults()</c>: the redactor must be registered ahead of
    /// the exporters, and the cap must wrap the logger factory after Chapter 23's redaction has replaced it.
    /// </summary>
    public static bool AddBeaconContentGate(this IHostApplicationBuilder builder)
    {
        var enableSensitiveData = builder.Environment.IsDevelopment()
            && builder.Configuration.GetValue<bool>("Beacon:Telemetry:EnableSensitiveData");

        if (!enableSensitiveData)
        {
            builder.Services.CapLogLevel("Microsoft.Extensions.AI", LogLevel.Debug);
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new ErrorDetailRedactor()));
        }

        return enableSensitiveData;
    }
}
