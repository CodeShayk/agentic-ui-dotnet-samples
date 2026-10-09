using Beacon.AI;
using Beacon.Domain;
using Ch05.Beacon.Pipeline;
using Microsoft.Extensions.AI;

namespace Ch05.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so one test project can host
// both of this chapter's agents side by side.
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI's JSON configuration and the Server-Sent Events formatter (Chapter 4).
        builder.Services.AddAGUIHosting();

        // Beacon's data and its read-only tools. Chapter 9 looks at the tool-call events they produce.
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        IList<AITool> serverTools = new BeaconTools(store).CreateReadOnlyTools();
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];

        // Optional: a cap on output tokens. Leave it unset for reasoning models (section 5.4).
        var maxOutputTokens = builder.Configuration.GetValue<int?>("Beacon:MaxOutputTokens");

        // The agent: the model chosen by configuration (AI:Provider), wrapped in layers the server owns.
        // The first layer added is the outermost: requests pass through them top to bottom.
        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()))
            .UseClientInstructionFilter()
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;

                // Server tools first. A client-declared tool may not reuse a server tool's name.
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];

                options.MaxOutputTokens = maxOutputTokens;
            })
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging()
            .UseOpenTelemetry();

        var app = builder.Build();

        // POST /agent: RunAgentInput in, AG-UI events out.
        app.MapAGUIEndpoint("/agent");

        app.Run();
    }
}
