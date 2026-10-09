using AGUI.Server;
using Beacon.AI;
using Beacon.Domain;
using Ch09.Beacon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace Ch09.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch09.Tests can host it in-process.
public sealed class Program
{
    /// <summary>The DI key of the model client at the bottom of the pipeline. Tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAGUIHosting();

        // Beacon's data, the slow back office in front of it, who Beacon acts for, and the tools.
        builder.Services.AddSingleton<IFieldServiceStore, InMemoryFieldServiceStore>();
        builder.Services.AddSingleton(services => SimulatedBackOffice.FromConfiguration(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(services => CurrentUser.FromConfiguration(services.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<BackendTools>();

        // The model: the scripted one by default, with this chapter's rules in front of Beacon's defaults.
        var ai = AIClientFactory.ReadOptions(builder.Configuration);
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (_, _) =>
            AIClientFactory.CreateChatClient(ai, [.. DeskScripts.Rules, .. BeaconScripts.Default]));

        // The scripted model sends each tool call whole; this makes it stream the arguments the way
        // OpenAI-style providers do, so the Desk can preview them (section 9.5).
        var streamArguments = ai.Provider == AIProvider.Scripted && builder.Configuration.GetValue("Beacon:StreamToolArguments", true);
        var fragmentDelay = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Beacon:ArgumentFragmentDelayMilliseconds", 40));

        // Chapter 5's pipeline. The first layer added is the outermost.
        builder.Services.AddChatClient(services => services.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use((inner, services) => new ConfigureOptionsChatClient(inner, services.GetRequiredService<BackendTools>().Configure))
            .UseFunctionInvocation(configure: functions =>
            {
                functions.TerminateOnUnknownCalls = true;   // a client's tool ends the run (Chapter 10)
                functions.AllowConcurrentInvocation = true; // calls from one model turn run side by side
            })
            .UseLogging()
            .Use(inner => streamArguments ? new ScriptedArgumentStreaming(inner, fragmentDelay) : inner);

        var app = builder.Build();

        // Each piece of a call's arguments goes on the wire as its own TOOL_CALL_ARGS event.
        app.MapAGUIEndpoint("/agent", endpoint => endpoint.StreamOptions = _ =>
            new AGUIStreamOptions().MapStreamingToolCallArguments(ToolArgumentFragments.Extract));

        app.Run();
    }
}
