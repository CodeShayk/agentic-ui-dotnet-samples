using AGUI.Abstractions;
using Beacon.AGUI.Hosting;
using Beacon.AI;
using Beacon.Domain;
using Ch19.Beacon.Agent.Capabilities;
using Ch19.Beacon.Agent.Enforcement;
using Ch19.Beacon.Agent.Scripts;
using Ch19.Beacon.Agent.Versioning;
using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Agent;

// Program lives in a namespace (not top-level statements) so Ch19.Tests can host it beside the Desk.
public sealed class Program
{
    /// <summary>The DI key of the model client at the bottom of the pipeline. Tests replace it.</summary>
    public const string ModelKey = "beacon-model";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;

        builder.Services.AddAGUIHosting();
        builder.Services.AddAGUIProtobuf(); // so transport.httpBinary: true is a true statement

        // Which deployment this process is: the declaration and the enforcement both read it.
        var deployment = BeaconDeployment.For(configuration.GetValue("Beacon:Profile", CapabilityProfile.Full));
        builder.Services.AddSingleton(deployment);
        builder.Services.AddSingleton<ProtocolVersionGate>();
        builder.Services.AddSingleton<DeclaredInputGuard>();

        // Beacon's read-only tools; the scripted model by default, with this chapter's rules first.
        var store = new InMemoryFieldServiceStore();
        builder.Services.AddSingleton<IFieldServiceStore>(store);
        IReadOnlyList<AIFunction> serverTools = [.. new BeaconTools(store).CreateReadOnlyTools().OfType<AIFunction>()];
        HashSet<string> serverToolNames = [.. serverTools.Select(t => t.Name)];
        builder.Services.AddKeyedSingleton<IChatClient>(ModelKey, (_, _) =>
            AIClientFactory.CreateChatClient(configuration, [.. CapabilityScripts.Rules, .. BeaconScripts.Default]));

        // Chapter 5's pipeline. The first layer added is the outermost, so the downgrade for clients that
        // declare no version sees everything below it, including subagent events a coordinator would add.
        builder.Services.AddChatClient(services => services.GetRequiredKeyedService<IChatClient>(ModelKey))
            .Use((inner, services) => new SubagentDowngradeChatClient(inner, services.GetRequiredService<ILogger<SubagentDowngradeChatClient>>()))
            .Use((inner, services) => new ClientInstructionFilterChatClient(inner, services.GetService<ILogger<ClientInstructionFilterChatClient>>()))
            .Use(inner => new DeclaredOutputChatClient(inner, deployment))
            .ConfigureOptions(options =>
            {
                options.Instructions = BeaconInstructions.Default;
                options.Tools = [.. serverTools, .. (options.Tools ?? []).Where(t => !serverToolNames.Contains(t.Name))];
            })
            .UseFunctionInvocation(configure: functions =>
            {
                functions.TerminateOnUnknownCalls = true; // a client's tool ends the run (Chapter 10)
                functions.MaximumIterationsPerRequest = deployment.MaxToolIterations; // declared as execution.maxIterations
            })
            .UseLogging();

        var app = builder.Build();

        app.MapAgentCapabilities("/agent/capabilities", BeaconCapabilities.Describe(deployment, serverTools));

        // Chapter 4's glue, declaring the version this agent speaks on every RUN_STARTED.
        app.MapAGUIEndpoint("/agent", options => options.ProtocolVersion = AGUIProtocol.Version)
            .AddEndpointFilter(async (context, next) =>
            {
                // Both checks run after the input is read and before the handler, so a refusal is an HTTP
                // error with no stream: the run never begins.
                var input = context.Arguments.OfType<RunAgentInput>().Single();
                var services = context.HttpContext.RequestServices;
                return services.GetRequiredService<ProtocolVersionGate>().Admit(input)
                    ?? services.GetRequiredService<DeclaredInputGuard>().Admit(input)
                    ?? await next(context);
            });

        app.Run();
    }
}
