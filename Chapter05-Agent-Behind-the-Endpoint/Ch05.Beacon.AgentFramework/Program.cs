using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Ch05.Beacon.Pipeline;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

namespace Ch05.Beacon.AgentFramework;

public sealed class Program
{
    /// <summary>The agent's name. MapAGUIServer looks up a session store registered under it.</summary>
    public const string AgentName = "beacon";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI JSON serialization for MapAGUIServer. It configures nothing else:
        // no authentication, authorization or session isolation (Chapter 23).
        builder.Services.AddAGUIServer();

        // In 1.23.0-preview.260928.1, AddAGUIServer appends AG-UI's resolver behind ASP.NET Core's
        // reflection resolver, so unset optional fields go out as null ("parentRunId": null), which
        // AG-UI 1.0 forbids. Putting the SDK's resolver first restores the 1.0 wire shape.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));

        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);

        // The same model and middleware as Ch05.Beacon.Agent, without ConfigureOptions:
        // in Agent Framework the agent owns its instructions and tools.
        builder.Services.AddChatClient(sp => AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()))
            .UseClientInstructionFilter()
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging()
            .UseOpenTelemetry();

        // Sessions are off by default, so this host keeps the same stateless contract as
        // Ch05.Beacon.Agent: the client sends the whole conversation on every run.
        // With Beacon:PersistSessions=true the server keeps each thread's history instead, and
        // clients must send only the new messages (section 5.6). Shared, unisolated sessions are
        // for a single-user demo only; Chapter 23 adds authorization and isolation.
        if (builder.Configuration.GetValue<bool>("Beacon:PersistSessions"))
        {
            builder.Services.AddKeyedSingleton<AgentSessionStore>(AgentName, new InMemoryAgentSessionStore());
        }

        var app = builder.Build();

        AIAgent beacon = app.Services.GetRequiredService<IChatClient>().AsAIAgent(
            instructions: BeaconInstructions.Default,
            name: AgentName,
            description: "Contoso Climate's field-service assistant.",
            tools: new BeaconTools(store).CreateReadOnlyTools(),
            loggerFactory: app.Services.GetRequiredService<ILoggerFactory>(),
            services: app.Services);

        // POST /agent: the same contract as Ch05.Beacon.Agent, through Agent Framework's host.
        app.MapAGUIServer("/agent", beacon);

        app.Run();
    }
}
