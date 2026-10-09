using System.Net;
using AGUI.Client;
using Ch25.Beacon.Console;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;

namespace Ch25.Tests;

/// <summary>The console takes its OTLP endpoint as an argument, so one command works in Bash and PowerShell.</summary>
public sealed class ConsoleTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_OTLP_endpoint_comes_from_an_argument_or_the_standard_variable()
    {
        var fromArgument = ConsoleArguments.Parse(
            ["--otlp", "http://localhost:4317", "--ask", "Show me the fault history for EQ-501."], _ => null);
        Assert.Equal(("http://localhost:4317", ConsoleArguments.DefaultEndpoint), (fromArgument.Otlp, fromArgument.Endpoint));

        var fromVariable = ConsoleArguments.Parse(
            ["http://beacon.contoso.test/agent"], name => name == "OTEL_EXPORTER_OTLP_ENDPOINT" ? "http://collector:4317" : null);
        Assert.Equal(("http://collector:4317", "http://beacon.contoso.test/agent"), (fromVariable.Otlp, fromVariable.Endpoint));
    }

    [Fact]
    public void The_consoles_chat_span_never_carries_the_conversation_even_when_the_environment_asks()
    {
        // Left unset, EnableSensitiveData would follow this variable, which is easy to leave on after debugging.
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");
        try
        {
            using var http = new HttpClient();
            using var beacon = ClientTelemetry.Instrument(new AGUIChatClient(new AGUIChatClientOptions(http, ConsoleArguments.DefaultEndpoint)));

            Assert.False(beacon.GetService<OpenTelemetryChatClient>()!.EnableSensitiveData);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", null);
        }
    }

    [Fact]
    public async Task The_console_exports_its_spans_to_the_endpoint_it_was_given()
    {
        // A stand-in OTLP/gRPC receiver: HTTP/2 without TLS, recording the paths it was asked for.
        List<string> paths = [];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        await using var receiver = builder.Build();
        receiver.Run(context =>
        {
            lock (paths)
            {
                paths.Add(context.Request.Path);
            }

            return Task.CompletedTask;
        });
        await receiver.StartAsync(Ct);

        using (ClientTelemetry.Start(receiver.Urls.Single(), showSpans: false))
        {
            using var ask = ClientTelemetry.Source.StartActivity("beacon.console.ask");
        } // disposing the provider flushes the exporter

        lock (paths)
        {
            Assert.Contains("/opentelemetry.proto.collector.trace.v1.TraceService/Export", paths);
        }

        await receiver.StopAsync(Ct);
    }
}
