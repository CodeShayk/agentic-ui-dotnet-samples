using AGUI.Abstractions;
using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Ch25.Beacon.Agent.FaultArchive;
using Ch25.Beacon.Agent.Pipeline;
using Ch25.Beacon.Agent.Scripting;
using Ch25.Beacon.Agent.Telemetry;
using Microsoft.Extensions.AI;

namespace Ch25.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so the test project can host it
// next to other entry points.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the Server-Sent Events formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Traces, metrics and logs, and the decision about message content (sections 25.2 and 25.3).
        var telemetry = builder.AddBeaconTelemetry();

        // Beacon's data and read-only tools, with get_fault_history served by the fault archive over HTTP.
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        builder.Services.AddFaultArchive(builder.Configuration);
        IList<AITool> serverTools =
        [
            .. new BeaconTools(store).CreateReadOnlyTools().Where(t => t.Name != BeaconTools.GetFaultHistoryName),
            FaultArchiveTool.Create(),
        ];
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        // Prices for the run-cost estimate (section 25.4).
        builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));

        // Chapter 5's pipeline, plus the cost of each run and the content decision on UseOpenTelemetry.
        // The scripted model now reports token usage, as real providers do.
        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(
                sp.GetRequiredService<IConfiguration>(),
                MeteredScripts.WithUsage(BeaconScripts.Default)))
            .UseClientInstructionFilter()
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseRunCost()
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging()
            .UseOpenTelemetry(configure: otel => otel.EnableSensitiveData = telemetry.EnableSensitiveData);

        var app = builder.Build();

        // RUN_FINISHED's usage names the provider; the model's own metadata knows it ("beacon" for the scripted model).
        var usageProvider = app.Services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>()?.ProviderName ?? "beacon";

        // POST /agent: RunAgentInput in, AG-UI events out, with the protocol version on RUN_STARTED
        // (Chapter 19) and token usage on the terminal event.
        app.MapAGUIEndpoint("/agent", endpoint =>
        {
            endpoint.ProtocolVersion = AGUIProtocol.Version;
            endpoint.StreamOptions = _ => new AGUIStreamOptions().WithUsageProvider(usageProvider);
        });

        // The stand-in fault archive that get_fault_history calls.
        app.MapFaultArchive();

        app.Logger.LogInformation(
            "Telemetry exporter: {Exporter}; message content in telemetry: {SensitiveData}; usage provider: {Provider}",
            telemetry.Exporter, telemetry.EnableSensitiveData ? "on" : "off", usageProvider);

        app.Run();
    }
}
