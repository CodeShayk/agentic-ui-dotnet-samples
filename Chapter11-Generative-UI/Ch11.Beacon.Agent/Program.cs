using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Ch11.Beacon.Agent.Components;
using Ch11.Beacon.Components;
using Microsoft.Extensions.AI;

namespace Ch11.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch11.Tests can host it in-process next to
// Beacon Desk.
public sealed class Program
{
    /// <summary>The DI key of the model client at the bottom of the pipeline. Tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();

        // Beacon's data, the server's checks on component props and everything the model is offered.
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton<ComponentRules>();
        builder.Services.AddSingleton<BeaconToolbox>();

        // The model: the scripted one by default, with this chapter's rules in front of Beacon's defaults.
        var ai = AIClientFactory.ReadOptions(builder.Configuration);
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (_, _) =>
            AIClientFactory.CreateChatClient(ai, [.. ComponentScripts.Rules, .. BeaconScripts.Default]));

        // Chapter 9's argument streaming for the scripted model, so components can render as they're written.
        var streamArguments = ai.Provider == AIProvider.Scripted && builder.Configuration.GetValue("Beacon:StreamToolArguments", true);
        var fragmentDelay = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Beacon:ArgumentFragmentDelayMilliseconds", 40));
        var fragmentLength = builder.Configuration.GetValue("Beacon:ArgumentFragmentLength", 24);

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(services => services.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, services) => new ConfigureOptionsChatClient(inner, services.GetRequiredService<BeaconToolbox>().Configure))
            .Use((inner, services) => new AllowListChatClient(inner, BeaconComponents.All, services.GetRequiredService<ILogger<AllowListChatClient>>()))
            .UseFunctionInvocation(configure: fic => fic.TerminateOnUnknownCalls = true)
            .UseLogging()
            .Use(inner => streamArguments ? new ScriptedArgumentStreaming(inner, fragmentDelay, fragmentLength) : inner);

        var app = builder.Build();

        // Each piece of a call's arguments goes on the wire as its own TOOL_CALL_ARGS event.
        app.MapAGUIEndpoint("/agent", endpoint => endpoint.StreamOptions = _ =>
            new AGUIStreamOptions().MapStreamingToolCallArguments(ToolArgumentFragments.Extract));

        app.Run();
    }
}
