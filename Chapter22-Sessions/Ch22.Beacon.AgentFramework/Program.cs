using AGUI.Abstractions;
using Beacon.AI;
using Beacon.Domain;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

namespace Ch22.Beacon.AgentFramework;

public sealed class Program
{
    /// <summary>The agent's name. MapAGUIServer looks up the session store registered under it.</summary>
    public const string AgentName = "beacon";

    /// <summary>The DI key of the raw model client; tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // AG-UI serialization, with the SDK's resolver first so optional fields stay off the wire (Chapter 5).
        builder.Services.AddAGUIServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AGUIJsonUtilities.DefaultTypeInfoResolver));

        // Sessions keyed by thread ID, kept in files. Registered under the agent's name, the store is used as is:
        // no isolation provider, so this host is for one user. Chapter 23 adds claims-based isolation.
        var sessions = Path.GetFullPath(builder.Configuration["Beacon:Sessions:Directory"] ?? "App_Data/sessions", builder.Environment.ContentRootPath);
        builder.Services.AddKeyedSingleton<AgentSessionStore>(AgentName, new FileAgentSessionStore(sessions));

        var store = new InMemoryFieldServiceStore();
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (sp, _) => AIClientFactory.CreateChatClient(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use(inner => new RunStartedDeclarationChatClient(inner)) // outermost: declares protocolVersion (Chapter 19)
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging();

        var app = builder.Build();

        AIAgent beacon = app.Services.GetRequiredService<IChatClient>().AsAIAgent(
            instructions: BeaconInstructions.Default,
            name: AgentName,
            description: "Contoso Climate's field-service assistant.",
            tools: new BeaconTools(store).CreateReadOnlyTools(),
            loggerFactory: app.Services.GetRequiredService<ILoggerFactory>(),
            services: app.Services);

        // The session holds the history, so clients send only their new messages (Chapter 5).
        app.MapAGUIServer("/agent", beacon);

        app.Run();
    }
}
