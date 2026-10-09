using System.Text.Json.Schema;
using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Ch12.Beacon.Agent.Sheet;
using Microsoft.Extensions.AI;

namespace Ch12.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch12.Tests can host it beside the Desk.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();

        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        var toolDelay = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Beacon:ToolDelayMilliseconds", 0));
        IList<AITool> readOnlyTools = new BeaconTools(store).CreateReadOnlyTools();
        HashSet<string> serverToolNames = [.. readOnlyTools.Select(t => t.Name), .. SheetTools.Names];

        // Chapter 5's pipeline with the shared sheet above the function invoker. The first layer added is the outermost.
        builder.Services.AddChatClient(services => AIClientFactory.CreateChatClient(
                services.GetRequiredService<IConfiguration>(), [.. SheetScripts.Rules, .. BeaconScripts.Default]))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. readOnlyTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .Use((inner, services) => new SharedSheetChatClient(inner, store, toolDelay, services.GetRequiredService<ILogger<SharedSheetChatClient>>()))
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging();

        var app = builder.Build();

        // State tools' results become STATE_SNAPSHOT and STATE_DELTA; the filter rejects bad state up front.
        app.MapAGUIEndpoint("/agent", endpoint => endpoint.StreamOptions = _ => SheetTools.MapToState(new AGUIStreamOptions()))
            .AddEndpointFilter(new SheetStateFilter());

        // The same contract the filter enforces, published for client developers.
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(SheetJsonContext.Default.SheetState);
        app.MapGet("/agent/state-schema", () => Results.Text(schema.ToJsonString(), "application/schema+json"));

        app.Run();
    }
}
