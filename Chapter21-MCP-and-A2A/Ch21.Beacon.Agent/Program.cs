using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch21.Beacon.Agent.Interop;
using Microsoft.Extensions.AI;

namespace Ch21.Beacon.Agent;

// Program lives in a namespace so the chapter's test project can host all three servers side by side.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the Server-Sent Events formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Beacon's own data and read-only tools, as in Chapter 5.
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        IList<AITool> serverTools = new BeaconTools(store).CreateReadOnlyTools();
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        // The systems behind the agent: manuals over MCP, Northwind Supply's agent over A2A.
        builder.Services.AddBeaconInterop(builder.Configuration);

        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(
                sp.GetRequiredService<IConfiguration>(),
                [.. InteropScripts.Rules, .. BeaconScripts.Default]))
            .UseClientInstructionFilter()
            .ConfigureOptions(options =>
            {
                options.Instructions = InteropInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseInteropTools()          // adds check_supplier_stock and the allow-listed MCP tools
            .UseSubagentAttribution()   // reports check_supplier_stock as a subagent on the AG-UI stream
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging()
            .UseOpenTelemetry();

        var app = builder.Build();

        // Chapter 19's capability document, with the remote tools declared in Beacon's words.
        app.MapAgentCapabilities("/agent/capabilities", InteropCapabilities.Describe(
            serverTools.OfType<AIFunction>(), app.Services.GetRequiredService<SupplierAgentTool>().AsAIFunction()));

        // POST /agent: RunAgentInput in, AG-UI events out, each RUN_STARTED declaring AG-UI 1.0 (Chapter 19).
        // The client never learns MCP or A2A exist.
        app.MapAGUIEndpoint("/agent", o => o.ProtocolVersion = AGUIProtocol.Version);

        app.Run();
    }
}
